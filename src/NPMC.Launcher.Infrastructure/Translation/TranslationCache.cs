using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.Infrastructure.Translation;

/// <summary>
/// 翻译缓存：**一个可读、可手改的 CSV** + 一个纯元数据 JSON。
///
/// 为什么换成 CSV：原来是「每条译文一个 sha1.txt」，文件名是哈希、内容只有译文，
/// 既看不出对应哪段文字，也没法校对或批量修改（实测 7 个文件全是黑盒）。
/// 现在每行是「语言 + 原文 + 译文 + 时间」，可以直接用 Excel / 文本编辑器维护。
///
/// 元数据 JSON 只写 schema 版本 / 条目数 / 最后整理时间，**不参与查找** ——
/// 存 key→行号 之类索引会在手改 CSV 后错位，所以查找一律用内存里的记录表。
///
/// 并发：整个进程只有一个 TranslationClient，读写都用同一把锁；追加写 + 定期整体重写（原子替换）。
/// </summary>
public sealed class TranslationCache
{
    /// <summary>CSV 列顺序。第一行是表头，方便 Excel 直接打开。</summary>
    private static readonly string[] Columns = { "key", "lang", "source", "translated", "updatedAt" };

    private const int SchemaVersion = 1;

    /// <summary>超过这个条目数就按时间保留最近的，防止无限增长。</summary>
    private const int MaxEntries = 5000;

    private readonly string _directory;
    private readonly string _csvPath;
    private readonly string _metaPath;
    private readonly object _gate = new();

    /// <summary>key → 记录；key = sha1(语言|原文)，与旧版缓存文件同名规则一致。</summary>
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);

    private bool _loaded;
    private int _dirtySinceCompaction;

    /// <summary>CSV 读进来时没有表头/结构不对 → 下次写入前整体重写，避免在坏文件后面接着追加。</summary>
    private bool _needsRewrite;

    public TranslationCache(string directory)
    {
        _directory = directory;
        _csvPath = Path.Combine(directory, "translations.csv");
        _metaPath = Path.Combine(directory, "meta.json");
    }

    public string CsvPath => _csvPath;

    public int Count
    {
        get
        {
            EnsureLoaded();
            lock (_gate) return _records.Count;
        }
    }

    public bool TryGet(string key, out string translation)
    {
        translation = string.Empty;
        if (string.IsNullOrEmpty(key)) return false;

        EnsureLoaded();

        lock (_gate)
        {
            if (_records.TryGetValue(key, out var record) && !string.IsNullOrWhiteSpace(record.Translation))
            {
                translation = record.Translation;
                return true;
            }
        }

        return false;
    }

    /// <summary>写入一条（同一 key 已存在则覆盖），并顺带更新元数据。</summary>
    public void Store(string key, string lang, string source, string translation)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(translation)) return;

        EnsureLoaded();

        lock (_gate)
        {
            var existed = _records.ContainsKey(key);
            var record = new Record(lang, source, translation, DateTimeOffset.UtcNow);
            _records[key] = record;

            try
            {
                Directory.CreateDirectory(_directory);

                if (_needsRewrite)
                {
                    CompactLocked();
                    _needsRewrite = false;
                    _dirtySinceCompaction = 0;
                    WriteMeta(compactedAt: DateTimeOffset.UtcNow);
                    return;
                }

                AppendRow(key, record);
                if (!existed) _dirtySinceCompaction++;

                // 每 200 条整理一次，顺手把过旧/超量的行淘汰掉
                if (_dirtySinceCompaction >= 200)
                {
                    CompactLocked();
                    _dirtySinceCompaction = 0;
                }

                WriteMeta(compactedAt: null);
            }
            catch
            {
                // 落盘失败不影响显示
            }
        }
    }

    /// <summary>旧版 `<sha1>.txt` 的懒迁移：命中时把原文一起补进 CSV，然后删掉旧文件。</summary>
    public bool TryImportLegacy(string legacyPath, string key, string lang, string source, out string translation)
    {
        translation = string.Empty;

        try
        {
            if (!File.Exists(legacyPath)) return false;

            var text = File.ReadAllText(legacyPath).Trim();
            if (text.Length == 0) return false;

            translation = text;
            Store(key, lang, source, text);

            try
            {
                File.Delete(legacyPath);
            }
            catch
            {
                // 删不掉就留着，下次还会再试
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;

        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(_csvPath)) return;

                var text = File.ReadAllText(_csvPath, Encoding.UTF8);
                if (text.Trim().Length == 0) return;

                var hasHeader = HasHeader(text);
                foreach (var row in ReadRows(text))
                {
                    if (row.Count < 4 || row[0].Length == 0 || row[3].Length == 0) continue;

                    var at = row.Count >= 5 && DateTimeOffset.TryParse(row[4], out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue;

                    // 早期版本把 "语言|原文" 直接当 key 写进了 CSV：转成 sha1，
                    // 否则这些行永远命中不了（查找用的是哈希），还会把整段原文塞在 key 列里。
                    var key = row[0].Contains('|') ? Sha1(row[0]) : row[0];
                    if (key != row[0]) _needsRewrite = true;

                    _records[key] = new Record(row[1], row[2], row[3], at);
                }

                // 没有表头、或内容看着不像我们的 CSV → 下次写入前整体重写
                if (!hasHeader)
                {
                    _needsRewrite = true;
                }

                if (_records.Count > MaxEntries)
                {
                    CompactLocked();
                }
            }
            catch
            {
                // CSV 坏了就当空缓存，下次写入时整体重写
                _needsRewrite = true;
            }
        }
    }

    /// <summary>按 updatedAt 保留最近的 MaxEntries 条，原子替换 CSV。</summary>
    private void CompactLocked()
    {
        try
        {
            var rows = _records
                .Select(pair => (Key: pair.Key, pair.Value))
                .OrderByDescending(pair => pair.Value.UpdatedAt)
                .Take(MaxEntries)
                .OrderBy(pair => pair.Value.UpdatedAt)
                .ToList();

            var builder = new StringBuilder();
            AppendHeader(builder);
            foreach (var (key, record) in rows)
            {
                AppendRow(builder, key, record);
            }

            var tempPath = _csvPath + ".tmp";
            File.WriteAllText(tempPath, builder.ToString(), Utf8Bom);
            File.Move(tempPath, _csvPath, overwrite: true);

            var keep = rows.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _records.Clear();
            foreach (var (key, record) in keep)
            {
                _records[key] = record;
            }

            WriteMeta(compactedAt: DateTimeOffset.UtcNow);
        }
        catch
        {
            // 整理失败不影响已有数据
        }
    }

    private void AppendRow(string key, Record record)
    {
        var builder = new StringBuilder();

        if (!File.Exists(_csvPath) || new FileInfo(_csvPath).Length == 0)
        {
            AppendHeader(builder);
        }

        AppendRow(builder, key, record);
        File.AppendAllText(_csvPath, builder.ToString(), Utf8Bom);
    }

    private static void AppendHeader(StringBuilder builder)
        => builder.Append(string.Join(',', Columns)).Append("\r\n");

    private static void AppendRow(StringBuilder builder, string key, Record record)
    {
        builder
            .Append(Escape(key)).Append(',')
            .Append(Escape(record.Lang)).Append(',')
            .Append(Escape(record.Source)).Append(',')
            .Append(Escape(record.Translation)).Append(',')
            .Append(Escape(record.UpdatedAt.ToString("o")))
            .Append("\r\n");
    }

    private void WriteMeta(DateTimeOffset? compactedAt)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var meta = new
            {
                schema = SchemaVersion,
                entries = _records.Count,
                updatedAt = DateTimeOffset.UtcNow,
                compactedAt,
                note = "只记录元数据，查找以 translations.csv 为准；手改 CSV 后本文件会在下次写入时自动更新",
            };

            File.WriteAllText(_metaPath, JsonSerializer.Serialize(meta, MetaJsonOptions), Utf8Bom);
        }
        catch
        {
            // 元数据写不进去不影响缓存
        }
    }

    private static readonly JsonSerializerOptions MetaJsonOptions = new() { WriteIndented = true };

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>和 TranslationClient 保持同一套 key 规则：sha1(语言|原文)。</summary>
    public static string Sha1(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool HasHeader(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var firstLine = text.Split('\n')[0].TrimEnd('\r');
        return firstLine.StartsWith(Columns[0] + ",", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>CSV 字段转义：包含逗号 / 引号 / 换行时用引号包起来，内部引号双写。</summary>
    private static string Escape(string value)
    {
        if (value.Length == 0) return value;

        var needsQuote = value.Contains(',') || value.Contains('"')
                         || value.Contains('\n') || value.Contains('\r');

        return needsQuote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    /// <summary>按 RFC 4180 解析（支持引号内的逗号与换行，EOF 时未闭合的引号也会收尾）。</summary>
    private static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;

        if (text[0] == '\uFEFF') text = text[1..];

        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    break;

                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // 跳过表头
        if (rows.Count > 0 && rows[0].Count > 0
            && string.Equals(rows[0][0], Columns[0], StringComparison.OrdinalIgnoreCase))
        {
            rows.RemoveAt(0);
        }

        return rows;
    }

    private sealed record Record(string Lang, string Source, string Translation, DateTimeOffset UpdatedAt);
}
