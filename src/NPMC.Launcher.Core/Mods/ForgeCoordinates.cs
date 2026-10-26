using System.Text.Json;
using NPMC.Launcher.Core.Models;

namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// 旧版 Forge 安装包（1.13 之前）里 <c>install_profile.json</c> 的关键内容。
///
/// 为什么单独处理旧版：1.13 起 Forge 改用"processors"在安装时跑一堆 java 程序来生成
/// 合并后的 jar；而旧版只需要把内嵌的 universal jar 放到 libraries 下、
/// 再把 <c>versionInfo</c> 写成版本 JSON 即可 —— **完全不需要运行 java**，
/// 也就不受"低完整性进程跑不了子进程"之类环境问题的影响。
/// </summary>
public sealed record ForgeInstallerProfile(
    string VersionId,
    string MinecraftVersion,
    VersionJson Version,
    string RawVersionJson,
    string? UniversalJarEntry,
    bool RequiresProcessors)
{
    /// <summary>能否纯离线安装（旧版结构）。</summary>
    public bool CanInstallOffline => !RequiresProcessors && UniversalJarEntry is not null;
}

/// <summary>Forge 的 Maven 坐标与安装包地址。</summary>
public static class ForgeCoordinates
{
    public const string MavenBase = "https://maven.minecraftforge.net/";

    /// <summary>
    /// 安装包的 Maven 版本号候选。
    ///
    /// 命名规则在历史上变过：老版本是 <c>&lt;mc&gt;-&lt;forge&gt;-&lt;mc&gt;</c>（MC 版本出现两次），
    /// 新版本是 <c>&lt;mc&gt;-&lt;forge&gt;</c>。与其猜分界线，不如两个都试 ——
    /// 反正失败只是一个 404。
    /// </summary>
    public static IReadOnlyList<string> MavenVersionCandidates(string minecraftVersion, string forgeVersion)
    {
        var plain = $"{minecraftVersion}-{forgeVersion}";
        var legacy = $"{plain}-{minecraftVersion}";

        return new[] { plain, legacy };
    }

    /// <summary>安装包（installer.jar）的候选地址，按顺序尝试。</summary>
    public static IReadOnlyList<string> InstallerUrlCandidates(string minecraftVersion, string forgeVersion)
        => MavenVersionCandidates(minecraftVersion, forgeVersion)
            .Select(version => $"{MavenBase}net/minecraftforge/forge/{version}/forge-{version}-installer.jar")
            .ToArray();

    /// <summary>某个库坐标在 Forge Maven 下的相对路径（group:artifact:version[:classifier]）。</summary>
    public static string LibraryPath(string coordinate) => MavenPath.FromCoordinate(coordinate);

    /// <summary>
    /// 解析 <c>install_profile.json</c>。
    /// </summary>
    /// <param name="json">文件内容。</param>
    /// <param name="fallbackVersionId">profile 里没写 id 时使用。</param>
    public static ForgeInstallerProfile Parse(string json, string fallbackVersionId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("install_profile.json 不是 JSON 对象");

        // 1.13+ 才有 processors：那种结构没有可直接使用的 versionInfo，
        // 版本 JSON 在安装包内的 version.json 里，由官方安装器写出来。
        var requiresProcessors = root.TryGetProperty("processors", out var processors)
                                 && processors.ValueKind == JsonValueKind.Array
                                 && processors.GetArrayLength() > 0;

        var minecraftVersion = "";
        string? universalJar = null;

        if (root.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Object)
        {
            if (install.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.String)
                minecraftVersion = mc.GetString() ?? "";

            if (install.TryGetProperty("filePath", out var filePath) && filePath.ValueKind == JsonValueKind.String)
                universalJar = filePath.GetString();
        }

        // 1.13+ 的 install_profile 顶层直接写 minecraft / version（没有 install 块）
        if (root.TryGetProperty("minecraft", out var modernMc) && modernMc.ValueKind == JsonValueKind.String)
            minecraftVersion = modernMc.GetString() ?? minecraftVersion;

        var modernVersionId = root.TryGetProperty("version", out var modernVersion)
                              && modernVersion.ValueKind == JsonValueKind.String
            ? modernVersion.GetString() ?? ""
            : "";

        if (!root.TryGetProperty("versionInfo", out var versionInfo)
            || versionInfo.ValueKind != JsonValueKind.Object)
        {
            return new ForgeInstallerProfile(
                string.IsNullOrWhiteSpace(modernVersionId) ? fallbackVersionId : modernVersionId,
                minecraftVersion,
                new VersionJson(),
                "",
                universalJar,
                RequiresProcessors: true);
        }

        // versionInfo 本身就是一份完整的版本 JSON
        var rawJson = versionInfo.GetRawText();
        var version = VersionJsonSerializer.Parse(rawJson);

        var versionId = string.IsNullOrWhiteSpace(version.Id) ? fallbackVersionId : version.Id;

        return new ForgeInstallerProfile(
            versionId,
            string.IsNullOrWhiteSpace(minecraftVersion) ? version.InheritsFrom ?? "" : minecraftVersion,
            version,
            rawJson,
            universalJar,
            requiresProcessors);
    }
}
