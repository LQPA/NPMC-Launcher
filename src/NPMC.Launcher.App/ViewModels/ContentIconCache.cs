using System.Text.Json;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 记住"文件名 → 图标地址"。
///
/// 为什么需要：图标地址只能通过"识别"拿到（要算哈希再查平台）。
/// 但识别是一次网络操作，不该每次启动都做；而图标位图本身已经按 URL 缓存在磁盘上。
/// 于是把文件名与图标地址的对应关系也存下来 —— 下次启动时直接按 URL 命中磁盘缓存，
/// 不需要任何网络请求就能把图标显示出来。
/// </summary>
internal sealed class ContentIconCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _file;
    private readonly Dictionary<string, string> _urls = new(StringComparer.OrdinalIgnoreCase);

    public ContentIconCache(string cacheDirectory)
    {
        _file = Path.Combine(cacheDirectory, "content-icon-urls.json");
        Load();
    }

    public bool TryGetIconUrl(string fileName, out string url) => _urls.TryGetValue(fileName, out url!);

    public void Set(string fileName, string url)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(url)) return;
        _urls[fileName] = url;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_urls, Options));
        }
        catch
        {
            // 记不住不影响功能
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file));
            if (map is null) return;

            foreach (var pair in map) _urls[pair.Key] = pair.Value;
        }
        catch
        {
            // 文件损坏就当空
        }
    }
}
