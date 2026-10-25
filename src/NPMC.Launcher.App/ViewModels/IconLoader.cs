using Avalonia.Media.Imaging;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 按 URL 下载并缓存图标位图。
///
/// 为什么需要它：Avalonia 的 <c>Image.Source</c> 只接受 <see cref="Bitmap"/> 之类的对象，
/// 直接给一个网址字符串不会去下载。而每次搜索/切换详情都重新下载图标既慢又浪费，
/// 所以这里按 URL 缓存（失败也缓存为 null，避免反复重试 404）。
/// </summary>
internal sealed class IconLoader
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IconLoader(HttpClient http) => _http = http;

    public async Task<Bitmap?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_cache.TryGetValue(url, out var cached)) return cached;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(true);
            using var stream = new MemoryStream(bytes);

            var bitmap = new Bitmap(stream);
            _cache[url] = bitmap;
            return bitmap;
        }
        catch
        {
            // 图标缺失不该影响任何功能：记成 null 就不再重试
            _cache[url] = null;
            return null;
        }
    }
}
