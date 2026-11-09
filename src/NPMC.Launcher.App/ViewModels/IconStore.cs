using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 图标缓存：原来「每个图标一个 sha1.img」（实测 234 个文件 / 2.66 MB），
/// 现在压成两个文件 —— <c>icons.pack</c>（所有图标字节）+ <c>icons.idx.json</c>（url → 偏移/长度）。
///
/// 为什么值得：图标是 1–28 KB 的碎文件，数量随浏览记录无限增长；
/// 打包后目录里只有两个文件，读写都是按偏移取一段字节（解码那条路径本来就是字节流）。
///
/// 淘汰：按「最后使用时间」保留最多 <see cref="MaxEntries"/> 条 / <see cref="MaxBytes"/> 字节；
/// 淘汰只从索引里移除（pack 里留下死字节），死字节超过一半时整体重写 pack。
/// </summary>
public sealed class IconStore
{
    private const int MaxEntries = 600;
    private const long MaxBytes = 32L * 1024 * 1024;

    private readonly string _directory;
    private readonly string _packPath;
    private readonly string _indexPath;
    private readonly object _gate = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;
    private long _packBytes;

    public IconStore(string directory)
    {
        _directory = directory;
        _packPath = Path.Combine(directory, "icons.pack");
        _indexPath = Path.Combine(directory, "icons.idx.json");
    }

    public int Count
    {
        get { EnsureLoaded(); lock (_gate) return _entries.Count; }
    }

    public long PackBytes
    {
        get { EnsureLoaded(); lock (_gate) return _packBytes; }
    }

    public bool TryGet(string url, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(url)) return false;

        EnsureLoaded();

        lock (_gate)
        {
            if (!_entries.TryGetValue(url, out var entry)) return false;

            try
            {
                using var stream = new FileStream(_packPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Seek(entry.Offset, SeekOrigin.Begin);

                var buffer = new byte[entry.Length];
                var read = 0;
                while (read < buffer.Length)
                {
                    var chunk = stream.Read(buffer, read, buffer.Length - read);
                    if (chunk <= 0) break;
                    read += chunk;
                }

                if (read != buffer.Length) return false;

                // 顺带刷新使用时间（内存里；索引在下次写入时落盘）
                _entries[url] = entry with { LastUsed = DateTimeOffset.UtcNow };
                bytes = buffer;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Store(string url, byte[] bytes)
    {
        if (string.IsNullOrEmpty(url) || bytes.Length == 0) return;

        EnsureLoaded();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                var offset = File.Exists(_packPath) ? new FileInfo(_packPath).Length : 0;

                using (var stream = new FileStream(_packPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                _entries[url] = new Entry(offset, bytes.Length, DateTimeOffset.UtcNow);
                _packBytes = offset + bytes.Length;

                TrimLocked();
                SaveIndexLocked();
            }
            catch
            {
                // 缓存失败不影响显示
            }
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
                DeleteLegacyFilesLocked();

                if (File.Exists(_indexPath))
                {
                    var model = JsonSerializer.Deserialize<IndexModel>(File.ReadAllText(_indexPath));
                    if (model is { Version: 1 })
                    {
                        foreach (var (url, entry) in model.Entries)
                        {
                            if (entry.Length <= 0) continue;
                            _entries[url] = entry;
                        }
                    }
                }

                _packBytes = File.Exists(_packPath) ? new FileInfo(_packPath).Length : 0;

                // 索引说文件更长（pack 被外部截断）→ 丢掉越界的条目
                foreach (var url in _entries.Where(pair => pair.Value.Offset + pair.Value.Length > _packBytes)
                             .Select(pair => pair.Key).ToList())
                {
                    _entries.Remove(url);
                }
            }
            catch
            {
                _entries.Clear();
                _packBytes = 0;
            }
        }
    }

    /// <summary>按最后使用时间淘汰，必要时重写 pack（去掉死字节）。</summary>
    private void TrimLocked()
    {
        if (_entries.Count == 0) return;

        var ordered = _entries.OrderBy(pair => pair.Value.LastUsed).ToList();
        var removed = false;

        while (ordered.Count > MaxEntries)
        {
            _entries.Remove(ordered[0].Key);
            ordered.RemoveAt(0);
            removed = true;
        }

        var liveBytes = ordered.Sum(pair => (long)pair.Value.Length);
        while (ordered.Count > 0 && liveBytes > MaxBytes)
        {
            liveBytes -= ordered[0].Value.Length;
            _entries.Remove(ordered[0].Key);
            ordered.RemoveAt(0);
            removed = true;
        }

        if (!removed) return;

        // 死字节超过一半就重写，否则留着（避免每次淘汰都重写 32 MB）
        if (_packBytes > liveBytes * 2)
        {
            RewritePackLocked(ordered);
        }
    }

    private void RewritePackLocked(List<KeyValuePair<string, Entry>> ordered)
    {
        try
        {
            var tempPath = _packPath + ".tmp";

            long offset = 0;
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var source = File.Exists(_packPath)
                       ? new FileStream(_packPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                       : null)
            {
                foreach (var (url, entry) in ordered)
                {
                    if (source is null) break;

                    var buffer = new byte[entry.Length];
                    source.Seek(entry.Offset, SeekOrigin.Begin);

                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var chunk = source.Read(buffer, read, buffer.Length - read);
                        if (chunk <= 0) break;
                        read += chunk;
                    }

                    if (read != buffer.Length) continue;

                    output.Write(buffer, 0, buffer.Length);
                    _entries[url] = entry with { Offset = offset };
                    offset += buffer.Length;
                }
            }

            if (File.Exists(tempPath))
            {
                File.Move(tempPath, _packPath, overwrite: true);
                _packBytes = offset;
            }
        }
        catch
        {
            // 重写失败就保持现状：索引仍然指向旧偏移
        }
    }

    private void SaveIndexLocked()
    {
        try
        {
            var model = new IndexModel { Version = 1, Entries = _entries };
            var tempPath = _indexPath + ".tmp";

            Directory.CreateDirectory(_directory);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(model), new UTF8Encoding(false));
            File.Move(tempPath, _indexPath, overwrite: true);
        }
        catch
        {
            // 索引写不进去只影响下次启动的命中率
        }
    }

    /// <summary>旧版「sha1.img」无法反推 url（sha1 不可逆），只能删掉让它们重新下载。</summary>
    private void DeleteLegacyFilesLocked()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;

            foreach (var file in Directory.EnumerateFiles(_directory, "*.img"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 占用中就下次再删
                }
            }
        }
        catch
        {
            // 目录读不了就算了
        }
    }

    private sealed record Entry(long Offset, int Length, DateTimeOffset LastUsed);

    private sealed class IndexModel
    {
        public int Version { get; set; }

        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
    }
}
