using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Infrastructure.Minecraft;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>左侧导航的页面。</summary>
public enum NavPage
{
    Instances = 0,
    Downloads = 1,
    Accounts = 2,
    Settings = 3,
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

        // 愚人节版本在清单里与普通快照完全一样，年份与主题只能从已知清单补
        ThemeText = AprilFoolsVersions.Find(entry.Id)?.ThemeText ?? "";
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

    /// <summary>愚人节版本的"年份 · 主题"，例如 "2024 · 毒马铃薯"；其他版本为空。</summary>
    public string ThemeText { get; }

    public bool IsAprilFools => ThemeText.Length > 0;

    public string DisplayText => IsAprilFools
        ? $"{Id}（{ThemeText}）   {ReleasedText}"
        : $"{Id}   {ReleasedText}";
}
