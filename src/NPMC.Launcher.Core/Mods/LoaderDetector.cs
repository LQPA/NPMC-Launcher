using NPMC.Launcher.Core.Models;

namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// 从已安装的版本元数据里判断模组加载器。
///
/// 为什么必须判断：查 Modrinth 更新时必须按加载器过滤，否则会给 Forge 整合包
/// 推 Fabric 版本的模组 —— 下载下来根本加载不了。我们的实例目前只记录版本号，
/// 但版本 JSON 的 libraries 里能看出装的是哪个加载器。
/// </summary>
public static class LoaderDetector
{
    /// <summary>返回检测到的加载器（可能不止一个，例如 Quilt 兼容 Fabric 的场合）。</summary>
    public static IReadOnlyList<string> Detect(VersionJson version)
    {
        if (version?.Libraries is null) return Array.Empty<string>();

        var loaders = new List<string>();

        foreach (var library in version.Libraries)
        {
            var name = library.Name?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) continue;

            if (name.Contains("net.fabricmc:fabric-loader", StringComparison.Ordinal))
            {
                if (!loaders.Contains("fabric")) loaders.Add("fabric");
            }
            else if (name.Contains("org.quiltmc:quilt-loader", StringComparison.Ordinal))
            {
                if (!loaders.Contains("quilt")) loaders.Add("quilt");
            }
            else if (name.Contains("net.neoforged", StringComparison.Ordinal))
            {
                if (!loaders.Contains("neoforge")) loaders.Add("neoforge");
            }
            else if (name.Contains("net.minecraftforge", StringComparison.Ordinal))
            {
                if (!loaders.Contains("forge")) loaders.Add("forge");
            }
        }

        return loaders;
    }

    /// <summary>原版（没有装任何加载器）。</summary>
    public static bool IsVanilla(VersionJson version) => Detect(version).Count == 0;
}
