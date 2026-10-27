using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Core.Mods;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 一个内容文件的识别结果。持久化后，重启启动器也能直接显示来源徽标、R/B/A 与图标，
/// 不必再联网识别一遍。
/// </summary>
internal sealed record ContentMetadata(
    bool Identified,
    UpdateSource? Source,
    ReleaseChannel? Channel,
    string IconUrl)
{
    /// <summary>
    /// 平台上的项目标识（Modrinth 的 projectId/slug、CurseForge 的 modId）。
    ///
    /// 用来从本地列表一键跳到"这个项目的其他版本"：没有它就只能按文件名猜名字再搜一次，
    /// 同名/改名/中文名的情况下经常搜不到。旧缓存没有这个字段，反序列化后是空串。
    /// </summary>
    public string ProjectRef { get; init; } = "";

    /// <summary>
    /// 项目标题（如 "Sodium"）。本地列表里的名字是文件名，带版本号，填进搜索框既长又搜不准；
    /// 这个字段用来把搜索框/计划标题换成人看得懂的项目名。
    /// </summary>
    public string Title { get; init; } = "";
}

/// <summary>
/// 「文件名 → 识别结果」的持久化缓存（`cache/content-metadata.json`）。
///
/// 为什么按文件名为键：同一个文件在磁盘上的身份就是文件名，而识别本身依赖算哈希 +
/// 一次网络查询。缓存下来之后，打开页签可以立刻显示徽标与图标（离线也行），
/// 后台识别只用于补充新文件或纠正结果。
///
/// 旧的 `content-icon-urls.json` 只记图标地址；现在连认领来源与版本通道一起记，
/// 因为这两项同样需要联网才能得到，而它们才是用户判断"这是什么"的主要依据。
/// </summary>
internal sealed class ContentMetadataCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly Dictionary<string, ContentMetadata> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ContentMetadataCache(string cacheDirectory)
    {
        _file = Path.Combine(cacheDirectory, "content-metadata.json");
        Load();
    }

    public bool TryGet(string fileName, out ContentMetadata metadata)
        => _entries.TryGetValue(fileName, out metadata!);

    public void Set(string fileName, ContentMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        _entries[fileName] = metadata;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries, Options));
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

            var map = JsonSerializer.Deserialize<Dictionary<string, ContentMetadata>>(File.ReadAllText(_file), Options);
            if (map is null) return;

            foreach (var pair in map) _entries[pair.Key] = pair.Value;
        }
        catch
        {
            // 文件损坏就当空
        }
    }
}
