namespace NPMC.Launcher.Core.Content;

/// <summary>可管理的内容类型。</summary>
public enum ContentKind
{
    Mod = 0,
    ResourcePack = 1,
    ShaderPack = 2,

    /// <summary>数据包：1.13 起才有，且**按世界**生效（saves/&lt;世界&gt;/datapacks）。</summary>
    DataPack = 3,
}

public static class ContentKindExtensions
{
    /// <summary>该类型在实例游戏目录下的子目录名。</summary>
    public static string DirectoryName(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
            ContentKind.DataPack => "datapacks",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>界面上的中文名。</summary>
    public static string DisplayName(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => "模组",
        ContentKind.ResourcePack => "资源包",
        ContentKind.ShaderPack => "光影",
            ContentKind.DataPack => "数据包",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Modrinth 的 project_type 取值。</summary>
    public static string ModrinthProjectType(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.ShaderPack => "shader",
            ContentKind.DataPack => "datapack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// CurseForge 的 classId；CurseForge 没有光影分类，返回 null。
    /// （模组 6、资源包 12 —— 这两个是 CF 的固定枚举值。）
    /// </summary>
    public static int? CurseForgeClassId(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => 6,
        ContentKind.ResourcePack => 12,
        ContentKind.ShaderPack => null,
            ContentKind.DataPack => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// 是否需要按加载器过滤。
    ///
    /// **只有模组需要**。资源包与光影在 Modrinth 上的 loaders 是 "minecraft"，
    /// 若按 fabric/forge 过滤会一条都搜不到 —— 这是很容易踩的坑。
    /// </summary>
    public static bool UsesLoaderFilter(this ContentKind kind) => kind == ContentKind.Mod;

    /// <summary>
    /// 是否"按世界"存放。
    ///
    /// 数据包是 <c>saves/&lt;世界&gt;/datapacks</c>，所以界面上必须先选世界，
    /// 不能像模组那样直接对着实例根目录操作。
    /// </summary>
    public static bool IsPerWorld(this ContentKind kind) => kind == ContentKind.DataPack;

    /// <summary>该类型从哪个版本开始存在（数据包是 1.13）。</summary>
    public static Version? MinimumGameVersion(this ContentKind kind) => kind switch
    {
        ContentKind.DataPack => new Version(1, 13),
        _ => null,
    };
    /// <summary>
    /// 有没有「下载新 X」的入口（搜索 + 依赖计划 + 安装）。
    ///
    /// 四类都支持；数据包额外要求先进入某个世界（按世界存放，安装目标是
    /// <c>saves/&lt;世界&gt;/datapacks</c>，见 <see cref="IsPerWorld"/>）。
    /// </summary>
    public static bool SupportsDownload(this ContentKind kind) =>
        kind is ContentKind.Mod or ContentKind.ResourcePack or ContentKind.ShaderPack or ContentKind.DataPack;

    /// <summary>该类型是否可能带依赖（只有模组会）。</summary>
    public static bool HasDependencies(this ContentKind kind) => kind == ContentKind.Mod;

    /// <summary>
    /// 平台上的固定加载器取值（与实例无关）。数据包在 Modrinth 上的 loader 就是 "datapack"：
    /// 用它筛版本列表，才能把"同一个项目的模组版（.jar）"排除掉，只留数据包 zip。
    /// </summary>
    public static IReadOnlyList<string> PlatformLoaders(this ContentKind kind) =>
        kind == ContentKind.DataPack ? new[] { "datapack" } : Array.Empty<string>();
}
