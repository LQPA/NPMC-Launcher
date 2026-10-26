using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Mods;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 整合包实例的模组列表：默认折叠整合包自带的模组，只显示用户手动添加的，方便补/删自己的模组。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool _hidePackMods = true;
    private HashSet<string> _packFileNames = new(StringComparer.OrdinalIgnoreCase);
    private int _packModsHidden;

    private bool _isModDownloadMode;
    private string _modFilterQuery = "";

    /// <summary>模组页是否处于“下载新模组”模式（隐藏列表与本地筛选，显示搜索结果）。</summary>
    public bool IsModDownloadMode
    {
        get => _isModDownloadMode;
        private set
        {
            if (!SetProperty(ref _isModDownloadMode, value)) return;
            RaiseModViewChanged();
        }
    }

    /// <summary>列表上方搜索框：实时过滤当前模组列表（本地，不联网）。</summary>
    public string ModFilterQuery
    {
        get => _modFilterQuery;
        set
        {
            if (!SetProperty(ref _modFilterQuery, value)) return;
            _ = RefreshContentAsync();
        }
    }

    public ICommand OpenModDownloadCommand { get; private set; }
    public ICommand CloseModDownloadCommand { get; private set; }

    public bool ShowModList => ShowContentList && (!IsModsTab || !IsModDownloadMode);

    public bool IsSearchPanelVisible => ShowContentList && (!IsModsTab || IsModDownloadMode);

    public bool IsModFilterVisible => ShowContentList && IsModsTab && !IsModDownloadMode;

    private void InitializePackModCommands()
    {
        OpenModDownloadCommand = new RelayCommand(() => IsModDownloadMode = true);
        CloseModDownloadCommand = new RelayCommand(() => IsModDownloadMode = false);
    }

    private void RaiseModViewChanged()
    {
        OnPropertyChanged(nameof(ShowModList));
        OnPropertyChanged(nameof(IsSearchPanelVisible));
        OnPropertyChanged(nameof(IsModFilterVisible));
        OnPropertyChanged(nameof(IsContentEmptyOrNotList));
        RaisePaneVisibilityChanged();
    }
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

        if (!_hidePackMods && string.IsNullOrWhiteSpace(_modFilterQuery)) return;

        var before = entries.Count;

        if (_hidePackMods)
        {
            entries.RemoveAll(entry => _packFileNames.Contains(Path.GetFileName(entry.Path)));
            _packModsHidden = before - entries.Count;
        }

        if (!string.IsNullOrWhiteSpace(_modFilterQuery))
        {
            var query = _modFilterQuery.Trim();
            entries.RemoveAll(entry =>
                !Path.GetFileName(entry.Path).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    private List<NPMC.Launcher.Core.Content.ContentEntry> _modSnapshot = new();
    private readonly Dictionary<string, ContentEntryItem> _modItemCache = new(StringComparer.OrdinalIgnoreCase);
    private InstanceListItem? _modSnapshotInstance;

    private void RememberModSnapshot(InstanceListItem instance, IEnumerable<NPMC.Launcher.Core.Content.ContentEntry> entries)
    {
        _modSnapshotInstance = instance;
        _modSnapshot = entries.ToList();

        var paths = _modSnapshot.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _modItemCache.Keys.Where(key => !paths.Contains(key)).ToList())
            _modItemCache.Remove(key);
    }

    /// <summary>
    /// 用内存里的扫描结果重建列表：折叠/筛选只移动已有的行对象，不再重新扫盘、
    /// 也不重新创建 495 个 ContentEntryItem（否则每次开关都会把内存顶上去）。
    /// </summary>
    private void RebuildModContent()
    {
        if (!IsModsTab || _modSnapshotInstance is null) return;

        var info = InstanceModpackStore.Load(_modSnapshotInstance.GameDirectory);
        var packNames = info?.PackFiles
            .Select(file => file.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var packSource = info?.IsCurseForge == true ? UpdateSource.CurseForge : UpdateSource.Modrinth;
        var query = _modFilterQuery.Trim();

        Content.Clear();

        var hidden = 0;

        foreach (var entry in _modSnapshot)
        {
            var name = Path.GetFileName(entry.Path);
            var isPack = packNames.Contains(name);

            if (isPack && _hidePackMods)
            {
                hidden++;
                continue;
            }

            if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!_modItemCache.TryGetValue(entry.Path, out var item))
            {
                item = new ContentEntryItem(entry, ToggleAsync, DeleteAsync, UpgradeModAsync);
                _modItemCache[entry.Path] = item;
            }

            if (isPack) item.SetIdentification(packSource);

            Content.Add(item);
        }

        _packFileNames = packNames;
        _packModsHidden = hidden;

        OnPropertyChanged(nameof(IsContentEmpty));
        OnPropertyChanged(nameof(IsContentEmptyOrNotList));
        OnPropertyChanged(nameof(ContentEmptyHint));
        RaisePackModFilterChanged();
        RaiseCommandStates();
    }
    private void RaisePackModFilterChanged()
    {
        OnPropertyChanged(nameof(CanHidePackMods));
        OnPropertyChanged(nameof(PackModsHint));
        OnPropertyChanged(nameof(ContentEmptyHint));
    }
}
