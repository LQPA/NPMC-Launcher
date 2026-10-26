using System.Xml.Linq;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>
/// 列出某个游戏版本可用的 NeoForge 版本。
///
/// NeoForge 的版本号不直接写游戏版本，而是把游戏版本的前缀拿来当自己的前两段：
///   1.21.6 → 21.6.x      1.21.1 → 21.1.x      1.21 → 21.0.x
///   1.20.1 → 47.1.x（NeoForge 早期是 Forge 47 的分支，另有编号）
///   26.3   → 26.3.x（2026 之后的版本号不再带 "1." 前缀）
/// 所以先按"该游戏版本对应的前缀"过滤，再按数值排序。
/// </summary>
public static class NeoForgeVersions
{
    public const string MetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    /// <summary>1.20.1 时代的 NeoForge 还挂在 net/neoforged/forge 这个坐标下（版本形如 1.20.1-47.1.106）。</summary>
    public const string LegacyMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml";

    public static async Task<IReadOnlyList<ForgeLoaderVersion>> GetAsync(
        Func<string, CancellationToken, Task<string>> fetch,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var xml = await fetch(MetadataUrl, cancellationToken).ConfigureAwait(false);

        if (minecraftVersion != "1.20.1") return Parse(xml, minecraftVersion);

        // 1.20.1：主清单里没有，去老坐标找
        var legacyXml = await fetch(LegacyMetadataUrl, cancellationToken).ConfigureAwait(false);
        return ParseLegacy(legacyXml, minecraftVersion);
    }

    /// <summary>该游戏版本在 NeoForge maven 上的版本前缀（按优先级）。</summary>
    public static IReadOnlyList<string> PrefixesFor(string minecraftVersion)
    {
        var prefixes = new List<string>();

        // 1.20.1 是特例：NeoForge 当时沿用 Forge 的 47.x 编号
        if (minecraftVersion == "1.20.1") prefixes.Add("47.1.");

        if (minecraftVersion.StartsWith("1.", StringComparison.Ordinal))
        {
            var rest = minecraftVersion[2..];

            prefixes.Add(rest.Contains('.') ? rest + "." : rest + ".0.");
        }
        else
        {
            prefixes.Add(minecraftVersion + ".");
        }

        return prefixes;
    }

    public static IReadOnlyList<string> InstallerUrlCandidates(string neoforgeVersion)
    {
        // 1.20.1 的老坐标：net/neoforged/forge
        if (neoforgeVersion.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                $"https://maven.neoforged.net/releases/net/neoforged/forge/{neoforgeVersion}/forge-{neoforgeVersion}-installer.jar",
            };
        }

        return new[]
        {
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoforgeVersion}/neoforge-{neoforgeVersion}-installer.jar",
        };
    }

    /// <summary>老坐标（net/neoforged/forge）的版本清单：只留该游戏版本的条目。</summary>
    public static IReadOnlyList<ForgeLoaderVersion> ParseLegacy(string xml, string minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<ForgeLoaderVersion>();

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            return Array.Empty<ForgeLoaderVersion>();
        }

        var prefix = minecraftVersion + "-";
        var results = document.Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .Select(element => element.Value.Trim())
            .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(value => new ForgeLoaderVersion(value, minecraftVersion, value))
            .ToList() ?? new List<ForgeLoaderVersion>();

        results.Sort((left, right) => Compare(
            right.Loader.Split('-').Last(),
            left.Loader.Split('-').Last()));

        return results;
    }

    public static IReadOnlyList<ForgeLoaderVersion> Parse(string xml, string minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<ForgeLoaderVersion>();

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            return Array.Empty<ForgeLoaderVersion>();
        }

        var all = document.Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToList() ?? new List<string>();

        var prefixes = PrefixesFor(minecraftVersion);
        var results = new List<ForgeLoaderVersion>();

        foreach (var version in all)
        {
            if (!prefixes.Any(prefix => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new ForgeLoaderVersion(version, minecraftVersion, version));
        }

        // 新版本优先；带 -beta 的排在正式版后面（同一个核心版本内）
        results.Sort((left, right) => Compare(right.Loader, left.Loader));
        return results;
    }

    private static int Compare(string left, string right)
    {
        var leftCore = left.Split('-')[0];
        var rightCore = right.Split('-')[0];

        var a = leftCore.Split('.');
        var b = rightCore.Split('.');

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var segmentA = index < a.Length ? a[index] : "0";
            var segmentB = index < b.Length ? b[index] : "0";

            if (int.TryParse(segmentA, out var numberA) && int.TryParse(segmentB, out var numberB))
            {
                var comparison = numberA.CompareTo(numberB);
                if (comparison != 0) return comparison;
                continue;
            }

            var textComparison = string.CompareOrdinal(segmentA, segmentB);
            if (textComparison != 0) return textComparison;
        }

        // 核心版本相同：正式版（没有后缀）排前面
        var leftIsBeta = left.Contains('-', StringComparison.Ordinal);
        var rightIsBeta = right.Contains('-', StringComparison.Ordinal);

        if (leftIsBeta != rightIsBeta) return leftIsBeta ? -1 : 1;

        return string.CompareOrdinal(left, right);
    }
}
