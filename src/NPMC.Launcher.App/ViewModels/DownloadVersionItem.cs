using NPMC.Launcher.Infrastructure.Minecraft;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>左侧导航的页面。</summary>
public enum NavPage
{
    Instances = 0,
    Downloads = 1,
}

/// <summary>下载页的两个区域。</summary>
public enum DownloadMode
{
    Versions = 0,
    Modpacks = 1,
}

/// <summary>下载页里的一个可选版本。</summary>
public sealed class DownloadVersionItem
{
    public DownloadVersionItem(VersionManifestEntry entry)
    {
        Id = entry.Id;
        Type = entry.Type;
        ReleasedAt = entry.ReleaseTime;
    }

    public string Id { get; }

    /// <summary>release / snapshot / old_beta / old_alpha。</summary>
    public string Type { get; }

    public DateTimeOffset ReleasedAt { get; }

    public string TypeBadge => Type switch
    {
        "release" => "R",
        "snapshot" => "S",
        _ => "A",
    };

    /// <summary>与内容列表里的通道徽标同一套配色：正式绿 / 快照琥珀 / 旧版灰。</summary>
    public string TypeColor => Type switch
    {
        "release" => "#2E7D46",
        "snapshot" => "#94611A",
        _ => "#4D5560",
    };

    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "旧 Beta",
        "old_alpha" => "旧 Alpha",
        _ => Type,
    };

    public string ReleasedText => ReleasedAt == default
        ? ""
        : ReleasedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string DisplayText => $"{Id}   {ReleasedText}";
}
