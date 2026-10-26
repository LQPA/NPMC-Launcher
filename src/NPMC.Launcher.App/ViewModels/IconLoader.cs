using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using NPMC.Launcher.App.Diagnostics;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 按 URL 获取图标位图，**内存 + 磁盘双缓存**。
///
/// 为什么需要磁盘缓存：搜索结果每次都要显示图标，如果只放内存，
/// 每次重启启动器都要把所有图标重新下载一遍（8 个结果 = 8 次请求）。
/// 磁盘上用 URL 的 SHA1 作文件名，避免 URL 里的非法字符，也天然去重。
///
/// Avalonia 的 Image.Source 只接受 Bitmap，不会自己下载 —— 所以这一步是必须的。
///
/// **生命周期**：同一个 URL 只解码一次，Bitmap 可能被多行共用，所以不能由某一行
/// 直接 Dispose。这里用"缓存持有 + 行租约"两层引用：
///   - 缓存最多留 <see cref="MaxEntries"/> 条，淘汰时如果还有行在显示（租约 &gt; 0），
///     先把 Bitmap 移到"待释放"区，最后一行松手时再 Dispose；
///   - 行离开可视树时 `Release(url)`，没有租约、也不在缓存里的 Bitmap 立即释放。
///
/// 这一层是必要的：Avalonia/Skia 的位图是原生内存，托管堆只有十几 MB 时 GC
/// 不会因为"原生内存涨了"而运行，光靠丢引用要等很久才回收（实测滚完 495 行能挂 400MB+）。
/// </summary>
internal sealed class IconLoader
{
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly Action<string>? _log;
    private const int MaxEntries = 128;

    /// <summary>
    /// 解码目标宽度。界面最大只显示 56px（详情图标），高 DPI 留一点余量即可。
    /// Modrinth 图标常见 512×512 / 1024×1024；按原尺寸解码再缩到 26px，
    /// 会让 Skia 为每张图分配 1~4MB 原生内存，滚一遍 495 行能到几百 MB。
    /// </summary>
    private const int MaxDecodeWidth = 96;

    private readonly Dictionary<string, Bitmap?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    /// <summary>URL → 当前有多少行正在显示这张图（租约）。</summary>
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经被缓存淘汰/清空、但还有行在显示的位图；最后一个租约释放时统一 Dispose。</summary>
    private readonly Dictionary<string, List<Bitmap>> _evictedButLeased = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>内存缓存里有多少条（含"取不到"的 null 记录）；内存诊断用。</summary>
    public int CachedCount => _memory.Count;

    /// <summary>当前未结束的行租约数；内存诊断用。</summary>
    public int LeaseCount => _leases.Values.Sum();

    public IconLoader(HttpClient http, string cacheDirectory, Action<string>? log = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _log = log;
    }

    /// <summary>取图标（缓存直接返回）。搜索结果/详情这类"少数几个"走这里，不持有租约。</summary>
    public async Task<Bitmap?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_memory.TryGetValue(url, out var cached))
        {
            Touch(url);
            return cached;
        }

        try
        {
            var file = Path.Combine(_cacheDirectory, Hash(url) + ".img");

            if (File.Exists(file))
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(true);
                var cachedBitmap = Decode(bytes, $" (缓存 {file})");
                if (cachedBitmap is not null)
                {
                    Store(url, cachedBitmap);
                    return cachedBitmap;
                }
            }

            var downloaded = await _http.GetByteArrayAsync(url).ConfigureAwait(true);
            var bitmap = Decode(downloaded, $" ({url})");

            if (bitmap is not null)
            {
                // 落盘失败不影响显示
                try
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    await File.WriteAllBytesAsync(file, downloaded).ConfigureAwait(true);
                }
                catch
                {
                    // 忽略
                }
            }

            Store(url, bitmap);
            return bitmap;
        }
        catch
        {
            // 图标缺失不该影响任何功能：记成 null 就不再重试
            Store(url, null);
            return null;
        }
    }

    /// <summary>
    /// 给内容行取图标：在缓存基础上记一个租约，调用方必须在行离开可视树时
    /// `Release(url)`。取不到返回 null，不产生租约。
    /// </summary>
    public async Task<Bitmap?> AcquireAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var bitmap = await GetAsync(url);
        if (bitmap is null) return null;

        _leases[url] = _leases.TryGetValue(url, out var count) ? count + 1 : 1;
        return bitmap;
    }

    /// <summary>释放内容行对图标的租约；没有租约且已被缓存淘汰的位图会立即 Dispose。</summary>
    public void Release(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!_leases.TryGetValue(url, out var count)) return;

        if (count > 1)
        {
            _leases[url] = count - 1;
            return;
        }

        _leases.Remove(url);

        if (_evictedButLeased.Remove(url, out var orphans))
            foreach (var orphan in orphans)
                DisposeBitmap(orphan);
    }

    /// <summary>把最近用到的 URL 移到队首；超出上限就丢掉最旧的引用（磁盘缓存还在，需要时再读）。</summary>
    private void Store(string url, Bitmap? bitmap)
    {
        // 同一个 key 被替换（并发取同一 URL）时，旧值按同样的规则处理
        if (_memory.TryGetValue(url, out var previous) && previous is not null && !ReferenceEquals(previous, bitmap))
            Retire(url, previous);

        _memory[url] = bitmap;
        Touch(url);

        while (_memory.Count > MaxEntries && _order.Last is { } last)
        {
            _order.RemoveLast();
            var oldestUrl = last.Value;

            if (!_memory.Remove(oldestUrl, out var oldestBitmap) || oldestBitmap is null) continue;

            Retire(oldestUrl, oldestBitmap);
        }
    }

    /// <summary>缓存不再持有这张图：没有行在显示就立即释放，否则等最后一个租约松手。</summary>
    private void Retire(string url, Bitmap bitmap)
    {
        if (_leases.TryGetValue(url, out var count) && count > 0)
        {
            if (!_evictedButLeased.TryGetValue(url, out var orphans))
            {
                orphans = new List<Bitmap>();
                _evictedButLeased[url] = orphans;
            }

            orphans.Add(bitmap);
            return;
        }

        DisposeBitmap(bitmap);
    }

    private void Touch(string url)
    {
        if (_order.First is { } first && string.Equals(first.Value, url, StringComparison.OrdinalIgnoreCase))
            return;

        var node = _order.Find(url);
        if (node is not null) _order.Remove(node);

        _order.AddFirst(url);
    }

    /// <summary>
    /// 释放整份内存缓存（磁盘缓存保留）；换实例/离开列表时调用。
    /// **调用前必须先把所有行租约释放掉**，否则位图会进"待释放"区，等行松手才 Dispose。
    /// </summary>
    public void Clear()
    {
        foreach (var pair in _memory)
        {
            if (pair.Value is null) continue;
            Retire(pair.Key, pair.Value);
        }

        _memory.Clear();
        _order.Clear();
    }

    private void DisposeBitmap(Bitmap bitmap)
    {
        try
        {
            bitmap.Dispose();
            LiveBitmapTracker.MarkDisposed();
        }
        catch
        {
            // 已经释放过/平台已关闭都不影响功能
        }
    }

    private Bitmap? Decode(byte[] bytes, string source)
    {
        // 无头诊断（--dump-instances）没有 Avalonia Application / 渲染平台，Bitmap 必然抛
        // "Unable to locate IPlatformRenderInterface"。直接跳过，别把日志刷满 495 行。
        if (Application.Current is null) return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(
                stream, MaxDecodeWidth, BitmapInterpolationMode.HighQuality);
            LiveBitmapTracker.Track(bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   && ex.Message.Contains("IPlatformRenderInterface", StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            // 解码失败通常是格式不受支持（例如 WebP）—— 记下来，否则界面只会"什么都没有"
            _log?.Invoke($"图标解码失败（{bytes.Length} 字节，{Describe(bytes)}）{source} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>按头部字节判断格式，便于从日志直接看出是哪一类图失败。</summary>
    private static string Describe(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return "PNG";
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "JPEG";
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[8] == 'W') return "WebP";
        if (bytes.Length >= 3 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F') return "GIF";
        return "未知格式";
    }

    private static string Hash(string url)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
}
