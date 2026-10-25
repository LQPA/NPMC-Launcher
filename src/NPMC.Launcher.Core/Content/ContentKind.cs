namespace NPMC.Launcher.Core.Content;

/// <summary>可管理的内容类型。</summary>
public enum ContentKind
{
    Mod = 0,
    ResourcePack = 1,
    ShaderPack = 2,
}

public static class ContentKindExtensions
{
    /// <summary>该类型在实例游戏目录下的子目录名。</summary>
    public static string DirectoryName(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>界面上的中文名。</summary>
    public static string DisplayName(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => "模组",
        ContentKind.ResourcePack => "资源包",
        ContentKind.ShaderPack => "光影",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Modrinth 的 project_type 取值。</summary>
    public static string ModrinthProjectType(this ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.ShaderPack => "shader",
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// 是否需要按加载器过滤。
    ///
    /// **只有模组需要**。资源包与光影在 Modrinth 上的 loaders 是 "minecraft"，
    /// 若按 fabric/forge 过滤会一条都搜不到 —— 这是很容易踩的坑。
    /// </summary>
    public static bool UsesLoaderFilter(this ContentKind kind) => kind == ContentKind.Mod;

    /// <summary>该类型是否可能带依赖（只有模组会）。</summary>
    public static bool HasDependencies(this ContentKind kind) => kind == ContentKind.Mod;
}
