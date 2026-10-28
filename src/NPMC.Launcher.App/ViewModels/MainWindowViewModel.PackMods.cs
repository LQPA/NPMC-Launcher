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

        if (!_hidePackMods && string.IsNullOrWhiteSpace(_contentFilterQuery)) return;

        var before = entries.Count;

        if (_hidePackMods)
        {
            entries.RemoveAll(entry => _packFileNames.Contains(Path.GetFileName(entry.Path)));
            _packModsHidden = before - entries.Count;
        }

        if (!string.IsNullOrWhiteSpace(_contentFilterQuery))
        {
            var query = _contentFilterQuery.Trim();
            entries.RemoveAll(entry =>
                !Path.GetFileName(entry.Path).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    private List<NPMC.Launcher.Core.Content.ContentEntry> _modSnapshot = new();
    private readonly Dictionary<string, ContentEntryItem> _modItemCache = new(StringComparer.OrdinalIgnoreCase);
    private InstanceListItem? _modSnapshotInstance;

    /// <summary>
    /// 行图标最近使用队列。虚拟化面板可能保留大量行容器（实测 432 个），
    /// 只靠 Attach/Detach 释放不可靠；这里再给"已贴到行上的图标"一个硬上限，
    /// 超过就按最久未使用释放，保证内存不随容器数量增长。
    /// </summary>
    private readonly LinkedList<ContentEntryItem> _iconRows = new();

    private const int MaxIconRows = 96;

    private void TrackIconRow(ContentEntryItem item)
    {
        var node = _iconRows.Find(item);
        if (node is not null) _iconRows.Remove(node);

        _iconRows.AddFirst(item);

        while (_iconRows.Count > MaxIconRows && _iconRows.Last is { } last)
            ReleaseIcon(last.Value);
    }

    private void UntrackIconRow(ContentEntryItem item)
    {
        var node = _iconRows.Find(item);
        if (node is not null) _iconRows.Remove(node);
    }

    /// <summary>
    /// 行进入可视树时按需取图标（懒加载）。取不到就留空，不阻塞列表。
    /// 只对当前列表里的行生效：切换实例/页签后晚到的结果不会贴到新列表上。
    /// </summary>
    internal async Task EnsureIconAsync(ContentEntryItem item)
    {
        if (item.Icon is not null) return;
        if (!Content.Contains(item)) return;
        if (!_contentMetadata.TryGet(item.Entry.FileName, out var metadata)) return;
        if (string.IsNullOrWhiteSpace(metadata.IconUrl)) return;

        var icon = await _icons.AcquireAsync(metadata.IconUrl);

        // 等磁盘/网络这段时间里行可能已经滚走：只给还在可视树上的行贴图标，
        // 否则立刻把刚拿到的租约还掉，别让它挂在缓存之外。
        if (icon is not null && item.Icon is null && item.IsRowVisible && Content.Contains(item))
        {
            if (item.IconLeaseUrl is { } previousUrl) _icons.Release(previousUrl);

            item.Icon = icon;
            item.IconLeaseUrl = metadata.IconUrl;
            TrackIconRow(item);
        }
        else if (icon is not null)
        {
            _icons.Release(metadata.IconUrl);
        }
    }

    /// <summary>行离开可视树：归还租约并丢掉位图引用（磁盘/LRU 缓存还在，滚回来时秒开）。</summary>
    internal void ReleaseIcon(ContentEntryItem item)
    {
        item.IsRowVisible = false;
        UntrackIconRow(item);

        if (item.IconLeaseUrl is { } url)
        {
            item.IconLeaseUrl = null;
            _icons.Release(url);
        }

        if (item.Icon is not null) item.Icon = null;
    }
    /// <summary>
    /// 切换实例时释放图标内存：行对象还在，下次回到该实例再按需从磁盘缓存加载。
    /// 除了模组行缓存，还要清当前列表（资源包/光影行不在 _modItemCache 里）。
    /// </summary>
    private void ReleaseContentIcons()
    {
        // 先还清所有租约，再清缓存 —— 顺序反了会让 Bitmap 进"待释放"区，
        // 只能等下一次 GC，等于又回到"看起来没回收"。
        foreach (var item in _modItemCache.Values)
            ReleaseIcon(item);

        foreach (var item in Content)
            ReleaseIcon(item);

        _icons.Clear();
    }
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
        var query = _contentFilterQuery.Trim();

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
                item = new ContentEntryItem(entry, ToggleAsync, DeleteAsync, UpgradeModAsync, OpenDownloadForAsync);
                _modItemCache[entry.Path] = item;
            }

            if (isPack) item.SetIdentification(packSource);

            Content.Add(item);
        }

        _packFileNames = packNames;
        _packModsHidden = hidden;
        _contentFileTotal = _modSnapshot.Count;

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
