using System.Collections.ObjectModel;
using NPMC.Launcher.Core.Instances;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 整合包实例的模组列表：默认折叠整合包自带的模组，只显示用户手动添加的，方便补/删自己的模组。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool _hidePackMods = true;
    private HashSet<string> _packFileNames = new(StringComparer.OrdinalIgnoreCase);
    private int _packModsHidden;

    /// <summary>是否隐藏整合包自带模组（默认隐藏）。</summary>
    public bool HidePackMods
    {
        get => _hidePackMods;
        set
        {
            if (!SetProperty(ref _hidePackMods, value)) return;
            _ = RefreshContentAsync();
        }
    }

    /// <summary>当前实例是整合包，且确实有可以折叠的模组。</summary>
    public bool CanHidePackMods => SelectedInstance is { HasModpackInfo: true } && _packFileNames.Count > 0;

    public string PackModsHint => _packModsHidden > 0
        ? $"已折叠 {_packModsHidden} 个整合包自带模组，只显示手动添加的"
        : "";

    /// <summary>在内容扫描完成后按整合包清单过滤。</summary>
    private void ApplyPackModFilter(InstanceListItem? instance, Core.Content.ContentKind kind, List<Core.Content.ContentEntry> entries)
    {
        _packFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _packModsHidden = 0;

        if (kind != Core.Content.ContentKind.Mod || instance is null) return;

        var info = InstanceModpackStore.Load(instance.GameDirectory);
        if (info is null || info.PackFiles.Count == 0) return;

        _packFileNames = info.PackFiles
            .Select(file => file.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!_hidePackMods) return;

        var before = entries.Count;
        entries.RemoveAll(entry => _packFileNames.Contains(Path.GetFileName(entry.Path)));
        _packModsHidden = before - entries.Count;
    }

    private void RaisePackModFilterChanged()
    {
        OnPropertyChanged(nameof(CanHidePackMods));
        OnPropertyChanged(nameof(PackModsHint));
    }
}
