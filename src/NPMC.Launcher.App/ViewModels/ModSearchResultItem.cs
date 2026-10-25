using System.Windows.Input;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.Mods;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 搜索结果里的一行。
///
/// 两个平台的搜索结果要能放进同一个列表，所以这里只暴露"展示 + 安装所需的引用"：
/// <see cref="Source"/> 决定安装时走哪条解析链路，<see cref="ProjectRef"/> 是那条链路要的标识
/// （Modrinth 用 slug，CurseForge 用数字 modId 的字符串形式）。
/// </summary>
public sealed class ModSearchResultItem
{
    public ModSearchResultItem(
        UpdateSource source,
        string projectRef,
        string title,
        string slug,
        long downloads,
        Func<ModSearchResultItem, Task> install)
    {
        Source = source;
        ProjectRef = projectRef;
        Title = title;
        Slug = slug;
        Downloads = downloads;
        InstallCommand = new AsyncRelayCommand(() => install(this));
    }

    public UpdateSource Source { get; }

    public string ProjectRef { get; }

    public string Title { get; }

    public string Slug { get; }

    public long Downloads { get; }

    /// <summary>副标题：来源与下载量。</summary>
    public string MetaText => $"{SourceLabel} · {FormatDownloads(Downloads)} 次下载";

    /// <summary>与列表徽标一致的来源标记（M / C）。</summary>
    public string SourceBadge => Source == UpdateSource.CurseForge ? "C" : "M";

    public string SourceColor => Source == UpdateSource.CurseForge ? "#A93226" : "#2E7D46";

    public string SourceLabel => Source == UpdateSource.CurseForge ? "CurseForge" : "Modrinth";

    public ICommand InstallCommand { get; }

    private static string FormatDownloads(long downloads) => downloads switch
    {
        >= 1_000_000 => $"{downloads / 1_000_000d:N1}M",
        >= 1_000 => $"{downloads / 1_000d:N0}K",
        _ => downloads.ToString(),
    };
}

/// <summary>
/// 安装计划里可选的一个版本。
/// <see cref="Ref"/> 是各平台自己的标识（Modrinth 的 versionId / CurseForge 的 fileId），
/// 界面只负责把它传回给解析器，不关心它的含义。
/// </summary>
public sealed record InstallVersionChoice(string Ref, string Label, bool IsPrerelease)
{
    /// <summary>预发布版本在下拉里标出来，避免选错。</summary>
    public string DisplayLabel => IsPrerelease ? $"{Label}（beta）" : Label;
}

/// <summary>安装计划里的一行（主体或依赖）。</summary>
public sealed class InstallPlanLineItem
{
    public InstallPlanLineItem(InstallTarget target)
    {
        IsDependency = target.IsDependency;
        Label = target.IsDependency ? (target.Pinned ? "依赖·作者指定" : "依赖") : "主体";
        Detail = $"{target.DisplayName}  {target.VersionLabel}";
    }

    public bool IsDependency { get; }

    public string Label { get; }

    public string Detail { get; }

    /// <summary>主体用绿、依赖用橙色，与列表里的徽标配色保持一致。</summary>
    public string LabelColor => IsDependency ? "#E0A458" : "#7BC47F";
}
