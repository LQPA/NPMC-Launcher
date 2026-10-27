using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.CurseForge;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>整合包搜索结果的一行（Modrinth 或 CurseForge）。</summary>
public sealed class ModpackHitItem
{
    public ModpackHitItem(ModrinthSearchHit hit)
    {
        Source = "modrinth";
        Slug = hit.Slug;
        Title = hit.DisplayName;
        Description = hit.Description;
        IconUrl = hit.IconUrl;
        Downloads = hit.Downloads;
    }

    public ModpackHitItem(CurseForgeSearchHit hit)
    {
        Source = "curseforge";
        ModId = hit.ModId;
        Slug = hit.Slug;
        Title = hit.DisplayName;
        Description = hit.Summary;
        IconUrl = hit.LogoUrl;
        Downloads = hit.DownloadCount;
    }

    public string Source { get; }

    public int ModId { get; }

    public string Slug { get; }

    public string Title { get; }

    public string Description { get; }

    public string IconUrl { get; }

    public long Downloads { get; }

    public string DownloadText => Downloads switch
    {
        >= 1_000_000 => $"{Downloads / 1_000_000.0:0.#}M 下载",
        >= 1_000 => $"{Downloads / 1_000.0:0.#}k 下载",
        _ => $"{Downloads} 下载",
    };

    public override string ToString() => Title;
}

/// <summary>整合包的一个版本（下载页下拉里的一项）。</summary>
public sealed class ModpackVersionItem
{
    public ModpackVersionItem(ModrinthVersion version)
    {
        Id = version.Id;
        VersionNumber = version.VersionNumber;
        VersionType = version.VersionType;
        GameVersions = version.GameVersions;
        Loaders = version.Loaders;
    }

    public ModpackVersionItem(CurseForgeFile file)
    {
        Id = file.FileId.ToString();
        FileId = file.FileId;
        VersionNumber = string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName;
        VersionType = file.ReleaseType switch
        {
            2 => "beta",
            3 => "alpha",
            _ => "release",
        };

        string[] loaderNames = { "Forge", "Fabric", "Quilt", "NeoForge" };
        GameVersions = file.GameVersions
            .Where(value => !loaderNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Loaders = file.GameVersions
            .Where(value => loaderNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public string Id { get; }

    public int FileId { get; }

    public string VersionNumber { get; }

    public string VersionType { get; }

    public IReadOnlyList<string> GameVersions { get; }

    public IReadOnlyList<string> Loaders { get; }

    public bool IsPrerelease =>
        string.Equals(VersionType, "beta", StringComparison.OrdinalIgnoreCase)
        || string.Equals(VersionType, "alpha", StringComparison.OrdinalIgnoreCase);

    public string ChannelBadge => IsPrerelease
        ? (VersionType.StartsWith("a", StringComparison.OrdinalIgnoreCase) ? "A" : "B")
        : "R";

    public string ChannelColor => IsPrerelease
        ? (ChannelBadge == "A" ? "#A93226" : "#94611A")
        : "#2E7D46";

    public string DisplayText
    {
        get
        {
            var game = GameVersions.Count == 0 ? "?" : string.Join("/", GameVersions.Take(2));
            var loaders = Loaders.Count == 0 ? "vanilla" : string.Join("/", Loaders);

            return $"{VersionNumber} · {game} · {loaders}";
        }
    }

    public override string ToString() => DisplayText;
}
