using System.Windows.Input;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.Mods;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>搜索结果里的一行。</summary>
public sealed class ModSearchResultItem
{
    public ModSearchResultItem(ModrinthSearchHit hit, Func<ModSearchResultItem, Task> install)
    {
        Slug = hit.Slug;
        Title = hit.DisplayName;
        Description = hit.Description;
        Downloads = hit.Downloads;
        InstallCommand = new AsyncRelayCommand(() => install(this));
    }

    public string Slug { get; }

    public string Title { get; }

    public string Description { get; }

    public long Downloads { get; }

    /// <summary>副标题：slug 与下载量 —— 判断"是不是同一个东西"靠 slug，下载量是个粗略的可信度信号。</summary>
    public string MetaText => $"{Slug} · {FormatDownloads(Downloads)} 次下载";

    public ICommand InstallCommand { get; }

    private static string FormatDownloads(long downloads) => downloads switch
    {
        >= 1_000_000 => $"{downloads / 1_000_000d:N1}M",
        >= 1_000 => $"{downloads / 1_000d:N0}K",
        _ => downloads.ToString(),
    };
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
