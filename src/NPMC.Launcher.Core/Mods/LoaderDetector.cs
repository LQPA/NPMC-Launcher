using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Modpacks;

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

    /// <summary>
    /// 加载器 + 版本号，用来写 .mrpack 的 dependencies（<c>fabric-loader</c>/<c>quilt-loader</c>
    /// /<c>forge</c>/<c>neoforge</c>）。版本号从 libraries 的坐标里取：
    /// <c>net.fabricmc:fabric-loader:0.15.11</c> 取最后一段；Forge 的 <c>1.21.1-52.0.1</c>
    /// 要取 MC 版本之后的部分。
    /// </summary>
    public static (string Key, string Version)? DetectWithVersion(VersionJson version)
    {
        if (version?.Libraries is null) return null;

        foreach (var library in version.Libraries)
        {
            var name = library.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var parts = name.Split(':');
            if (parts.Length < 3) continue;

            var group = parts[0].ToLowerInvariant();
            var artifact = parts[1].ToLowerInvariant();
            var versionPart = parts[2];

            if (group == "net.fabricmc" && artifact == "fabric-loader")
                return ("fabric-loader", versionPart);

            if (group == "org.quiltmc" && artifact == "quilt-loader")
                return ("quilt-loader", versionPart);

            if (group == "net.neoforged" && (artifact == "neoforge" || artifact == "forge"))
                return ("neoforge", versionPart);

            if (group == "net.minecraftforge" && artifact == "forge")
            {
                // 形如 1.21.1-52.0.1：加载器版本在第一个 '-' 之后
                var dash = versionPart.IndexOf('-');
                return ("forge", dash >= 0 ? versionPart[(dash + 1)..] : versionPart);
            }
        }

        // 新版 NeoForge 的 profile 里不再有 net.neoforged:neoforge 这个库（改成 fancymodloader），
        // 加载器版本只出现在版本 id 上：neoforge-26.3.0.99 / 1.20.1-forge-47.2.0
        return DetectFromId(version.Id) ?? (version.InheritsFrom is { } parent ? DetectFromId(parent) : null);
    }

    /// <summary>
    /// 从版本 id 里抠加载器版本（库坐标拿不到时的兜底）。
    ///
    /// **统一转调 <see cref="LoaderVersionName.DetectLoader"/>**，不再自己写正则：
    /// 原来的正则有三个缺口 ——
    ///   - <c>fabric-loader-([\d.]+)</c> / <c>quilt-loader-([\d.]+)</c> 遇到带后缀的
    ///     加载器版本会截断（<c>0.31.0-beta.4</c> → <c>0.31.0</c>）；
    ///   - <c>neoforge-(\d[\d.]*)</c> 同样丢掉后缀；
    ///   - <c>(?:^|-)forge-(\d[\d.]*)</c> 只认新格式，老 Forge 的
    ///     <c>1.7.10-Forge10.13.4.1614-1.7.10</c> 完全匹配不到。
    /// 四家的格式差异集中在 <see cref="LoaderVersionName"/> 一处维护。
    /// </summary>
    private static (string Key, string Version)? DetectFromId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var info = LoaderVersionName.DetectLoader(id);
        if (info.LoaderKey == LoaderVersionInfo.Vanilla.LoaderKey) return null;

        // 与库坐标那条路保持同样的 key（"fabric-loader"/"quilt-loader"），
        // 这两者是写进 .mrpack dependencies 的值，不能因为走的哪条路而不同
        var key = info.LoaderKey switch
        {
            "fabric" => "fabric-loader",
            "quilt" => "quilt-loader",
            _ => info.LoaderKey,
        };

        return (key, info.LoaderVersion);
    }
}
