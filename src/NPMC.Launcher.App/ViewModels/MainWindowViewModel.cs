using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LauncherPaths _launcher;
    private readonly ContentManager _contentManager = new();

    private InstanceListItem? _selectedInstance;
    private ContentTabItem _selectedTab;
    private string _status = "正在读取实例…";
    private bool _isBusy;
    private int _contentScanVersion;

    public MainWindowViewModel(LauncherPaths launcher)
    {
        _launcher = launcher;

        ContentTabs = new ObservableCollection<ContentTabItem>
        {
            new("概览", null),
            new("模组", ContentKind.Mod),
            new("资源包", ContentKind.ResourcePack),
            new("光影", ContentKind.ShaderPack),
        };
        _selectedTab = ContentTabs[0];

        RefreshCommand = new AsyncRelayCommand(LoadAsync, onError: ex => Status = $"刷新失败：{ex.Message}");
        OpenGameFolderCommand = new RelayCommand(OpenGameDirectory, () => SelectedInstance is not null);
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder, () => SelectedInstance is not null);
        OpenContentFolderCommand = new RelayCommand(OpenContentFolder, () => CanBulk);
        DisableAllCommand = new AsyncRelayCommand(() => ToggleAllAsync(enable: false), () => CanBulk);
        EnableAllCommand = new AsyncRelayCommand(() => ToggleAllAsync(enable: true), () => CanBulk);
    }

    // ---- 顶部信息 ----

    public string RootPath => _launcher.Root;

    public string RootHint => $"用 --root <目录> 或环境变量 {LauncherRoot.EnvironmentVariable} 指定";

    // ---- 实例 ----

    public ObservableCollection<InstanceListItem> Instances { get; } = new();

    public InstanceListItem? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value)) return;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ShowOverview));
            OnPropertyChanged(nameof(ShowContentList));
            RaiseCommandStates();
            _ = RefreshContentAsync();
        }
    }

    public bool HasSelection => SelectedInstance is not null;

    public bool IsEmpty => Instances.Count == 0;

    // ---- 页签与内容 ----

    public ObservableCollection<ContentTabItem> ContentTabs { get; }

    public ContentTabItem SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || !SetProperty(ref _selectedTab, value)) return;

            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(IsContentList));
            OnPropertyChanged(nameof(ShowOverview));
            OnPropertyChanged(nameof(ShowContentList));
            OnPropertyChanged(nameof(ContentEmptyHint));
            RaiseCommandStates();
            _ = RefreshContentAsync();
        }
    }

    public bool IsOverview => SelectedTab.Kind is null;

    public bool IsContentList => SelectedTab.Kind is not null;

    /// <summary>右侧主体是否显示概览（同时要求已选中实例）。</summary>
    public bool ShowOverview => HasSelection && IsOverview;

    /// <summary>右侧主体是否显示内容列表（同时要求已选中实例）。</summary>
    public bool ShowContentList => HasSelection && IsContentList;

    public ObservableCollection<ContentEntryItem> Content { get; } = new();

    public bool IsContentEmpty => Content.Count == 0;

    public string ContentEmptyHint => SelectedTab.Kind switch
    {
        ContentKind.Mod => "mods 文件夹是空的。把 .jar 放进这个文件夹（或将来自动安装），就会出现在这里。",
        ContentKind.ResourcePack => "resourcepacks 文件夹是空的。支持 .zip 和文件夹两种形式。",
        ContentKind.ShaderPack => "shaderpacks 文件夹是空的。光影通常需要配合 Iris / OptiFine 使用。",
        _ => "",
    };

    // ---- 状态 ----

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ---- 命令 ----

    public ICommand RefreshCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand OpenModsFolderCommand { get; }
    public ICommand OpenContentFolderCommand { get; }
    public ICommand DisableAllCommand { get; }
    public ICommand EnableAllCommand { get; }

    private bool CanBulk => IsContentList && SelectedInstance is not null && Content.Count > 0;

    // ---- 实例加载 ----

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var catalog = new InstanceCatalog(await new JsonInstanceStore(_launcher).LoadAsync());

            Instances.Clear();
            foreach (var instance in catalog.Instances)
                Instances.Add(new InstanceListItem(_launcher, instance));

            OnPropertyChanged(nameof(IsEmpty));

            SelectedInstance = Instances.FirstOrDefault();

            var problems = catalog.Validate();
            Status = Instances.Count == 0
                ? "还没有实例。可用 npmc instance create 创建（界面创建在后续阶段接入）。"
                : problems.Count > 0
                    ? $"共 {Instances.Count} 个实例，其中 {problems.Count} 项数据有问题"
                    : $"共 {Instances.Count} 个实例";

            await RefreshContentAsync();
        }
        catch (Exception ex)
        {
            Instances.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            SelectedInstance = null;
            Status = $"读取实例失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- 内容加载 ----

    /// <summary>重新扫描当前页签对应的目录。公开是为了让无头诊断（--dump-instances）也能走同一条路径。</summary>
    public async Task RefreshContentAsync()
    {
        // 用版本号丢弃过期结果：快速切换实例/页签时，先发出的扫描可能后返回
        var version = ++_contentScanVersion;
        var instance = SelectedInstance;
        var kind = SelectedTab.Kind;

        await RefreshCountsAsync(instance);

        if (instance is null || kind is not { } contentKind)
        {
            if (version == _contentScanVersion)
            {
                Content.Clear();
                OnPropertyChanged(nameof(IsContentEmpty));
                RaiseCommandStates();
            }
            return;
        }

        IReadOnlyList<ContentEntry> entries;
        try
        {
            var directory = instance.DirectoryFor(contentKind);
            entries = await Task.Run(() => ContentScanner.Scan(directory, contentKind));
        }
        catch (Exception ex)
        {
            Status = $"读取内容失败：{ex.Message}";
            return;
        }

        if (version != _contentScanVersion) return;

        Content.Clear();
        foreach (var entry in entries)
            Content.Add(new ContentEntryItem(entry, ToggleAsync, DeleteAsync));

        OnPropertyChanged(nameof(IsContentEmpty));
        RaiseCommandStates();
    }

    /// <summary>把三个页签的数量都刷新一遍，这样页签上的数字始终准确。</summary>
    private async Task RefreshCountsAsync(InstanceListItem? instance)
    {
        // 左侧列表里的那一行也显示数量，一起刷新，避免和页签数字对不上
        instance?.RefreshContentCounts();

        foreach (var tab in ContentTabs)
        {
            if (tab.Kind is not { } kind)
            {
                tab.Count = 0;
                continue;
            }

            if (instance is null)
            {
                tab.Count = 0;
                continue;
            }

            var directory = instance.DirectoryFor(kind);
            tab.Count = await Task.Run(() => ContentScanner.Scan(directory, kind).Count);
        }
    }

    // ---- 内容操作 ----

    private async Task ToggleAsync(ContentEntryItem item)
    {
        if (SelectedInstance is null || SelectedTab.Kind is not { } kind) return;

        var wasEnabled = item.IsEnabled;
        var instance = SelectedInstance;

        try
        {
            var directory = instance.DirectoryFor(kind);
            var updated = await Task.Run(() => _contentManager.Toggle(item.Entry, directory));

            // 就地更新这一行：绝不重建集合，否则 ListBox 会丢失选中项（高亮乱跳）
            item.Update(updated);
            Status = wasEnabled ? $"已禁用 {updated.Name}" : $"已启用 {updated.Name}";
        }
        catch (Exception ex)
        {
            Status = $"操作失败：{ex.GetBaseException().Message}";
        }

        await RefreshCountsAsync(instance);
        RaiseCommandStates();
    }

    private async Task DeleteAsync(ContentEntryItem item)
    {
        if (SelectedInstance is null || SelectedTab.Kind is not { } kind) return;

        var instance = SelectedInstance;

        try
        {
            var directory = instance.DirectoryFor(kind);
            var movedTo = await Task.Run(() => _contentManager.Delete(item.Entry, directory));

            // 删除是这一项真的消失了，从集合移除即可（只影响这一行）
            Content.Remove(item);
            OnPropertyChanged(nameof(IsContentEmpty));

            Status = $"已移入回收站：{Path.GetFileName(movedTo)}（可从 .trash 恢复）";
        }
        catch (Exception ex)
        {
            Status = $"删除失败：{ex.GetBaseException().Message}";
        }

        await RefreshCountsAsync(instance);
        RaiseCommandStates();
    }

    private async Task ToggleAllAsync(bool enable)
    {
        if (SelectedInstance is null || SelectedTab.Kind is not { } kind) return;

        var instance = SelectedInstance;
        var directory = instance.DirectoryFor(kind);
        var targets = Content.Where(item => item.IsEnabled != enable).ToList();
        var changed = 0;

        try
        {
            foreach (var item in targets)
            {
                var updated = await Task.Run(() => _contentManager.Toggle(item.Entry, directory));
                item.Update(updated);
                changed++;
            }

            Status = changed == 0
                ? "没有需要变更的条目"
                : $"{(enable ? "已启用" : "已禁用")} {changed} 个条目";
        }
        catch (Exception ex)
        {
            Status = $"批量操作失败（已完成 {changed} 个）：{ex.GetBaseException().Message}";
        }

        await RefreshCountsAsync(instance);
        RaiseCommandStates();
    }

    // ---- 打开目录 ----

    private void OpenGameDirectory()
    {
        if (SelectedInstance is not null) OpenDirectory(SelectedInstance.GameDirectory);
    }

    private void OpenModsFolder()
    {
        if (SelectedInstance is not null) OpenDirectory(SelectedInstance.ModsDirectory);
    }

    private void OpenContentFolder()
    {
        if (SelectedInstance is null || SelectedTab.Kind is not { } kind) return;
        OpenDirectory(SelectedInstance.DirectoryFor(kind));
    }

    private void OpenDirectory(string directory)
    {
        string target;
        try
        {
            Directory.CreateDirectory(directory);
            // 只去掉"根目录之外"的尾部反斜杠："C:\" 仍是 "C:\"，
            // 否则会变成 "C:" 或被拼成 \" 导致命令行解析错误
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex)
        {
            Status = $"创建目录失败：{ex.GetBaseException().Message}（{directory}）";
            return;
        }

        // 首选 explorer.exe：把目录路径直接当 FileName 交给 ShellExecute，
        // 依赖 shell 去猜"这是个文件夹"，在部分环境下会失败或静默什么都不做。
        if (TryStartProcess("explorer.exe", $"\"{target}\"", out var explorerError)) return;

        // 兜底：仍旧让 shell 处理目录本身
        if (TryStartProcess(target, null, out var shellError)) return;

        Status = $"打开目录失败：explorer=({explorerError}) / shell=({shellError})  {target}";
    }

    private static bool TryStartProcess(string fileName, string? arguments, out string error)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };

            if (arguments is not null) startInfo.Arguments = arguments;

            System.Diagnostics.Process.Start(startInfo);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private void RaiseCommandStates()
    {
        (OpenGameFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenModsFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenContentFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DisableAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EnableAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
