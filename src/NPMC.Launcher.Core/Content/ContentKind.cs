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
}
