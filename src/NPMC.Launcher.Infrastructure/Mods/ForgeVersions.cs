using System.Xml.Linq;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>一个 Forge 加载器版本：<see cref="Loader"/> 是安装时用的版本号，<see cref="MavenVersion"/> 是 maven 坐标。</summary>
public sealed record ForgeLoaderVersion(string Loader, string Minecraft, string MavenVersion)
{
    public string DisplayText => Loader;

    public override string ToString() => DisplayText;
}

/// <summary>
/// 列出某个游戏版本可用的 Forge 加载器。
///
/// 数据源是 Forge 自己的 maven-metadata.xml（一个 XML，几百行），
/// 里面同时有老坐标（<c>&lt;mc&gt;-&lt;loader&gt;-&lt;mc&gt;</c>）和新坐标（<c>&lt;mc&gt;-&lt;loader&gt;</c>），
/// 所以先按"以 &lt;mc&gt;- 开头"筛，再把重复的 mc 段去掉。
///
/// 为什么不用 CurseForge / Modrinth 的加载器列表：它们只覆盖有整合包的版本，
/// 而且要多一次 API 调用；Forge 的 maven 才是"存在即可用"的权威来源。
/// </summary>
public static class ForgeVersions
{
    public const string MetadataUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

    public static async Task<IReadOnlyList<ForgeLoaderVersion>> GetAsync(
        Func<string, CancellationToken, Task<string>> fetch,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var xml = await fetch(MetadataUrl, cancellationToken).ConfigureAwait(false);
        return Parse(xml, minecraftVersion);
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

        var prefix = minecraftVersion + "-";
        var results = new List<ForgeLoaderVersion>();

        foreach (var mavenVersion in all)
        {
            if (!mavenVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var loader = mavenVersion[prefix.Length..];

            // 老坐标：1.7.10-10.13.4.1614-1.7.10 —— 末尾再重复一次 mc 版本
            var suffix = "-" + minecraftVersion;
            if (loader.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                loader = loader[..^suffix.Length];

            if (loader.Length == 0) continue;
            if (loader.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(new ForgeLoaderVersion(loader, minecraftVersion, mavenVersion));
        }

        // 新版本优先：把 a.b.c 里的数字段按数值比，避免 "47.1.0" 排在 "47.10.0" 后面
        results.Sort((left, right) => CompareLoader(right.Loader, left.Loader));
        return results;
    }

    private static int CompareLoader(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var segmentA = index < a.Length ? a[index] : "0";
            var segmentB = index < b.Length ? b[index] : "0";

            var numericA = int.TryParse(segmentA, out var numberA);
            var numericB = int.TryParse(segmentB, out var numberB);

            if (numericA && numericB)
            {
                var comparison = numberA.CompareTo(numberB);
                if (comparison != 0) return comparison;
                continue;
            }

            var textComparison = string.CompareOrdinal(segmentA, segmentB);
            if (textComparison != 0) return textComparison;
        }

        return 0;
    }
}
