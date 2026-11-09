using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.Infrastructure.Translation;

/// <summary>
/// 机器翻译（把模组简介翻成用户的语言），带内存 + 磁盘缓存。
///
/// 用的是 Google 的公共 gtx 端点：**不需要 API key**，代价是它不属于正式 API
/// （没有 SLA、可能限流或失效）。因此这里的设计原则是"翻不出来就不显示"，
/// 绝不因为翻译失败而影响详情本身。
///
/// 缓存格式见 <see cref="TranslationCache"/>：一个可手改的 CSV（语言 + 原文 + 译文）
/// 加一个纯元数据 JSON。旧版「sha1.txt」会在命中时懒迁移进 CSV 后删除。
/// </summary>
public sealed class TranslationClient
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    /// <summary>单次翻译的字符上限。超长正文会先截断 —— 详情摘要本来就只显示前几百字。</summary>
    private const int MaxCharacters = 1800;

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TranslationCache _cache;
    private readonly Dictionary<string, string?> _memory = new(StringComparer.Ordinal);

    public TranslationClient(HttpClient http, string cacheDirectory)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _cache = new TranslationCache(cacheDirectory);
    }

    /// <summary>缓存文件路径（诊断用）。</summary>
    public string CachePath => _cache.CsvPath;

    public async Task<string?> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (string.IsNullOrWhiteSpace(targetLanguage)) return null;

        var normalized = text.Length > MaxCharacters ? text[..MaxCharacters] : text;
        var key = $"{targetLanguage}|{normalized}";

        if (_memory.TryGetValue(key, out var cached)) return cached;

        // 1) 新格式 CSV
        if (_cache.TryGet(key, out var fromCsv))
        {
            _memory[key] = fromCsv;
            return fromCsv;
        }

        // 2) 旧版 <sha1>.txt：命中时补进 CSV 并删掉旧文件
        var legacyPath = Path.Combine(_cacheDirectory, Hash(key) + ".txt");
        if (_cache.TryImportLegacy(legacyPath, key, targetLanguage, normalized, out var migrated))
        {
            _memory[key] = migrated;
            return migrated;
        }

        var result = await RequestAsync(normalized, targetLanguage, cancellationToken).ConfigureAwait(true);
        _memory[key] = result;

        if (result is not null)
        {
            _cache.Store(key, targetLanguage, normalized, result);
        }

        return result;
    }

    private async Task<string?> RequestAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{Endpoint}?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}"
                      + $"&dt=t&q={Uri.EscapeDataString(text)}";

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(true);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 响应形如 [[["译文","原文",...], ...], null, "en", ...]
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            var segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var builder = new StringBuilder();

            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;

                var piece = segment[0];
                if (piece.ValueKind == JsonValueKind.String) builder.Append(piece.GetString());
            }

            var translated = builder.ToString().Trim();
            return translated.Length == 0 ? null : translated;
        }
        catch
        {
            // 端点不可用 / 限流 / 网络问题 —— 一律当作"没有翻译"，不冒泡
            return null;
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
