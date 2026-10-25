using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 按 URL 获取图标位图，**内存 + 磁盘双缓存**。
///
/// 为什么需要磁盘缓存：搜索结果每次都要显示图标，如果只放内存，
/// 每次重启启动器都要把所有图标重新下载一遍（8 个结果 = 8 次请求）。
/// 磁盘上用 URL 的 SHA1 作文件名，避免 URL 里的非法字符，也天然去重。
///
/// Avalonia 的 Image.Source 只接受 Bitmap，不会自己下载 —— 所以这一步是必须的。
/// </summary>
internal sealed class IconLoader
{
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Bitmap?> _memory = new(StringComparer.OrdinalIgnoreCase);

    public IconLoader(HttpClient http, string cacheDirectory, Action<string>? log = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _log = log;
    }

    public async Task<Bitmap?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_memory.TryGetValue(url, out var cached)) return cached;

        try
        {
            var file = Path.Combine(_cacheDirectory, Hash(url) + ".img");

            if (File.Exists(file))
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(true);
                var cachedBitmap = Decode(bytes, $" (缓存 {file})");
                _memory[url] = cachedBitmap;
                if (cachedBitmap is not null) return cachedBitmap;
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

            _memory[url] = bitmap;
            return bitmap;
        }
        catch
        {
            // 图标缺失不该影响任何功能：记成 null 就不再重试
            _memory[url] = null;
            return null;
        }
    }

    private Bitmap? Decode(byte[] bytes, string source)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
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
