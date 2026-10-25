using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Diagnostics;
using NPMC.Launcher.Infrastructure.Hashing;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Launch;
using NPMC.Launcher.Infrastructure.Modrinth;
using NPMC.Launcher.Infrastructure.Mods;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LauncherPaths _launcher;
    private readonly ContentManager _contentManager = new();
    private readonly DispatcherTimer _logTimer;

    // Modrinth / CurseForge 查询与文件下载共用同一个 HttpClient（连接池复用，UA 统一）
    private readonly HttpClient _http = HttpFileDownloader.CreateClient();
    private readonly HttpFileDownloader _downloader;
    private readonly ModsUpdateService _updateService;
    private readonly ModrinthClient _modrinth;
    private readonly ModInstaller _installer;
    private readonly CurseForgeClient? _curseForge;
    private readonly CurseForgeInstaller? _curseForgeInstaller;

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
            new("概览", DetailTabKind.Overview),
            new("模组", DetailTabKind.Content, ContentKind.Mod),
            new("资源包", DetailTabKind.Content, ContentKind.ResourcePack),
            new("光影", DetailTabKind.Content, ContentKind.ShaderPack),
            new("日志", DetailTabKind.Log),
        };
        _selectedTab = ContentTabs[0];

        // 日志页在显示时每 2 秒重新读取一次文件尾部；游戏运行时能看到实时输出
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logTimer.Tick += (_, _) => _ = RefreshLogAsync();

        _downloader = new HttpFileDownloader(_http);
        _updateService = new ModsUpdateService(_http);
        _modrinth = new ModrinthClient(_http);
        _installer = ModInstaller.Create(_http);

        if (CurseForgeClient.TryResolveApiKey(out var curseForgeKey, out _) && curseForgeKey is not null)
        {
            _curseForge = new CurseForgeClient(_http, curseForgeKey);
            _curseForgeInstaller = new CurseForgeInstaller(_curseForge);
        }

        RefreshCommand = new AsyncRelayCommand(LoadAsync, onError: ex => Status = $"刷新失败：{ex.Message}");

        OpenGameFolderCommand = new AsyncRelayCommand(OpenGameDirectory,
            () => SelectedInstance is not null, OnOpenError);
        OpenModsFolderCommand = new AsyncRelayCommand(OpenModsFolder,
            () => HasModsDirectory, OnOpenError);
        OpenResourcePacksFolderCommand = new AsyncRelayCommand(OpenResourcePacksFolder,
            () => HasResourcePacksDirectory, OnOpenError);
        OpenShaderPacksFolderCommand = new AsyncRelayCommand(OpenShaderPacksFolder,
            () => HasShaderPacksDirectory, OnOpenError);
        OpenContentFolderCommand = new AsyncRelayCommand(OpenContentFolder,
            () => IsContentList && SelectedInstance is not null, OnOpenError);

        DisableAllCommand = new AsyncRelayCommand(() => ToggleAllAsync(enable: false), () => CanBulk);
        EnableAllCommand = new AsyncRelayCommand(() => ToggleAllAsync(enable: true), () => CanBulk);

        CheckUpdatesCommand = new AsyncRelayCommand(CheckModUpdatesAsync, () => CanCheckUpdates);

        SearchCommand = new AsyncRelayCommand(SearchModsAsync, () => CanSearch);
        ConfirmInstallCommand = new AsyncRelayCommand(ConfirmInstallAsync, () => CanConfirmInstall);
        CancelPlanCommand = new RelayCommand(CancelPlan);
        ToggleSearchPanelCommand = new RelayCommand(() => IsSearchPanelExpanded = !IsSearchPanelExpanded);

        RefreshLogCommand = new AsyncRelayCommand(RefreshLogAsync, () => SelectedInstance is not null);

        LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync,
            () => SelectedInstance is not null && !IsGameRunning, OnOpenError);
    }

    // ---- 启动游戏 ----

    private bool _isGameRunning;

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (!SetProperty(ref _isGameRunning, value)) return;
            OnPropertyChanged(nameof(LaunchButtonText));
            RaiseCommandStates();
        }
    }

    public string LaunchButtonText => IsGameRunning ? "运行中…" : "启动游戏";

    public ICommand LaunchGameCommand { get; }

    private async Task LaunchGameAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var versionId = instance.VersionId;
        Status = $"正在准备启动 {instance.Name}…";

        PreparedLaunch prepared;
        try
        {
            var launcherSettings = await LoadLauncherSettingsAsync();

            // 启动不依赖网络：版本 JSON 在 install 阶段就已落盘
            var version = await GameLauncher.TryLoadInstalledVersionAsync(_launcher, versionId);
            if (version is null)
            {
                Status = $"版本 {versionId} 尚未安装（缺少 version json）。先运行 npmc install {versionId}";
                return;
            }

            prepared = GameLauncher.Prepare(_launcher, instance.Model, version, launcherSettings);
        }
        catch (Exception ex)
        {
            Status = $"准备启动失败：{ex.GetBaseException().Message}";
            return;
        }

        if (!prepared.CanLaunch)
        {
            Status = string.Join("；", prepared.Problems);
            return;
        }

        try
        {
            IsGameRunning = true;
            using var process = GameLauncher.Start(prepared);

            Status = $"游戏已启动（PID {process.Id}）  日志 {prepared.LatestLog}";

            // 不要用 EnableRaisingEvents：受限环境下打开父进程句柄会被拒绝。
            // WaitForExitAsync 在正常用户环境可用（本机已实测过真实启动）。
            await process.WaitForExitAsync();

            Status = process.ExitCode == 0
                ? $"游戏已正常退出（{instance.Name}）"
                : $"游戏异常退出，退出码 {process.ExitCode}，详见 {prepared.LatestLog}";

            if (IsLogTab) await RefreshLogAsync();
        }
        catch (Exception ex)
        {
            Status = $"启动失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"启动失败 {instance.Id} → {ex.GetBaseException().Message}");
        }
        finally
        {
            IsGameRunning = false;
        }
    }

    private async Task<LauncherSettings> LoadLauncherSettingsAsync()
    {
        try
        {
            return await new JsonSettingsStore(_launcher).LoadAsync();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    // ---- 顶部信息 ----

    public string RootPath => _launcher.Root;

    public string RootHint => $"用 --root <目录> 或环境变量 {LauncherRoot.EnvironmentVariable} 指定";

    /// <summary>
    /// 当前进程的完整性级别。
    ///
    /// 直接显示在标题栏下方：Low（S-1-16-4096）时系统不允许调起资源管理器，
    /// 有了这行，用户一眼就能确认"是环境受限"还是"功能坏了"，
    /// 也能区分窗口是从哪个终端启动的。
    /// </summary>
    public string IntegrityText => $"权限 {ProcessContext.Describe()}";

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
            OnPropertyChanged(nameof(ShowLog));

            // 概览页的可编辑字段都从 SelectedInstance 取值，切换实例时必须重读
            OnPropertyChanged(nameof(MemoryMb));
            OnPropertyChanged(nameof(WindowWidth));
            OnPropertyChanged(nameof(WindowHeight));
            SyncPresetFromInstance();

            RaiseFolderVisibilityChanged();
            RaiseCommandStates();
            UpdateLogTimer();

            // 搜索结果绑定在"实例的游戏版本/加载器"上，换实例后它们已不成立
            ResetSearchState();

            if (IsLogTab) _ = RefreshLogAsync();
            else _ = RefreshContentAsync();

            // 换实例 → 重新判断该实例能不能用模组/光影
            if (value is { } selectedInstance) _ = RefreshCapabilitiesAsync(selectedInstance);
        }
    }

    public bool HasSelection => SelectedInstance is not null;

    // ---- 实例能力（决定哪些内容页签可用）----

    private bool _hasModLoader;
    private bool _hasShaderSupport;
    private IReadOnlyList<string> _detectedLoaders = Array.Empty<string>();

    public bool HasModLoader => _hasModLoader;

    /// <summary>检测到的加载器名（fabric / forge / neoforge / quilt）。</summary>
    public string DetectedLoadersText =>
        _detectedLoaders.Count == 0 ? "无（原版）" : string.Join(", ", _detectedLoaders);

    /// <summary>
    /// 重新计算实例能力并刷新页签可用性。
    ///
    /// 为什么要禁用而不是隐藏：
    ///   - 隐藏会让人以为"这个启动器不支持模组"；禁用 + 提示能说清"缺的是加载器"。
    ///   - 模组需要加载器；光影还需要 Iris/OptiFine —— 原版客户端根本不读 shaderpacks。
    ///   - 资源包原版就支持，永远可用。
    /// </summary>
    public async Task RefreshCapabilitiesAsync(InstanceListItem instance)
    {
        var loaders = await DetectLoadersAsync(instance);

        var hasShaderProvider = false;
        try
        {
            if (Directory.Exists(instance.ModsDirectory))
            {
                hasShaderProvider = Directory
                    .EnumerateFiles(instance.ModsDirectory, "*.jar")
                    .Select(Path.GetFileName)
                    .Any(name => name is not null
                                 && (name.Contains("iris", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("optifine", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch
        {
            // 目录读不了就当没有
        }

        _detectedLoaders = loaders;
        _hasModLoader = loaders.Count > 0;
        _hasShaderSupport = _hasModLoader && hasShaderProvider;

        OnPropertyChanged(nameof(HasModLoader));
        OnPropertyChanged(nameof(DetectedLoadersText));

        ApplyTabAvailability();
    }

    private void ApplyTabAvailability()
    {
        foreach (var tab in ContentTabs)
        {
            if (tab.ContentKind is not { } kind) continue;

            var (enabled, hint) = kind switch
            {
                ContentKind.Mod => (
                    _hasModLoader,
                    "该实例没有安装模组加载器（Fabric / Forge / NeoForge / Quilt），"
                    + "模组不会被游戏加载。请先安装加载器。"),

                ContentKind.ShaderPack => (
                    _hasShaderSupport,
                    "该实例缺少光影支持（Iris 或 OptiFine），原版客户端不会读取 shaderpacks。"
                    + "安装 Iris 后即可使用。"),

                _ => (true, ""),
            };

            tab.SetAvailability(enabled, hint);
        }

        // 当前页签恰好被禁用（换了实例、或删掉了 Iris）→ 退回概览
        if (SelectedTab.ContentKind is { } selectedKind
            && ContentTabs.FirstOrDefault(tab => tab.ContentKind == selectedKind) is { IsEnabled: false })
        {
            SelectedTab = ContentTabs[0];
        }
    }

    public bool IsEmpty => Instances.Count == 0;

    // ---- 概览处的「打开目录」入口 ----
    // 只有目录真实存在才显示入口：用户没装光影就不该看到一个点了也只会新建空文件夹的按钮。

    public bool HasModsDirectory =>
        SelectedInstance is not null && Directory.Exists(SelectedInstance.ModsDirectory);

    public bool HasResourcePacksDirectory =>
        SelectedInstance is not null && Directory.Exists(SelectedInstance.ResourcePacksDirectory);

    public bool HasShaderPacksDirectory =>
        SelectedInstance is not null && Directory.Exists(SelectedInstance.ShaderPacksDirectory);

    // ---- 实例设置（概览页直接编辑）----

    private const int MinimalMemoryMb = 512;
    private const int MaximalMemoryMb = 65536;
    private const int MinimalWindowSide = 320;
    private const int MaximalWindowSide = 7680;

    private ResolutionPreset? _selectedPreset;    public decimal MinimalMemory => MinimalMemoryMb;
    public decimal MaximalMemory => MaximalMemoryMb;
    public decimal MinimalWindowSideValue => MinimalWindowSide;
    public decimal MaximalWindowSideValue => MaximalWindowSide;

    public ObservableCollection<ResolutionPreset> ResolutionPresets { get; } = new()
    {
        new("854 × 480", 854, 480),
        new("1024 × 768", 1024, 768),
        new("1280 × 720", 1280, 720),
        new("1366 × 768", 1366, 768),
        new("1600 × 900", 1600, 900),
        new("1920 × 1080", 1920, 1080),
        new("2560 × 1440", 2560, 1440),
        new("3840 × 2160", 3840, 2160),
    };

    /// <summary>内存（MB）。用 decimal? 是为了直接绑定 NumericUpDown.Value，省掉转换器。</summary>
    public decimal? MemoryMb
    {
        get => SelectedInstance?.MemoryMb;
        set
        {
            if (SelectedInstance is not { } item || value is not { } raw) return;

            var memory = Math.Clamp((int)Math.Round(raw), MinimalMemoryMb, MaximalMemoryMb);
            if (item.MemoryMb == memory) return;

            item.ApplySettings(item.Model.Settings with { MemoryMb = memory });
            OnPropertyChanged();
            ScheduleInstanceSave();
        }
    }

    public decimal? WindowWidth
    {
        get => SelectedInstance?.WindowWidth;
        set
        {
            if (SelectedInstance is not { } item || value is not { } raw) return;

            var width = Math.Clamp((int)Math.Round(raw), MinimalWindowSide, MaximalWindowSide);
            if (item.WindowWidth == width) return;

            item.ApplySettings(item.Model.Settings with { WindowWidth = width });
            OnPropertyChanged();
            SyncPresetFromInstance();
            ScheduleInstanceSave();
        }
    }

    public decimal? WindowHeight
    {
        get => SelectedInstance?.WindowHeight;
        set
        {
            if (SelectedInstance is not { } item || value is not { } raw) return;

            var height = Math.Clamp((int)Math.Round(raw), MinimalWindowSide, MaximalWindowSide);
            if (item.WindowHeight == height) return;

            item.ApplySettings(item.Model.Settings with { WindowHeight = height });
            OnPropertyChanged();
            SyncPresetFromInstance();
            ScheduleInstanceSave();
        }
    }

    /// <summary>下拉选择预设分辨率：选中即填入宽高，其余逻辑和手动输入一致。</summary>
    public ResolutionPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            // 下拉在实例切换时会被清空（赋 null），那不算用户操作
            if (!SetProperty(ref _selectedPreset, value) || value is null) return;
            if (SelectedInstance is not { } item) return;

            item.ApplySettings(item.Model.Settings with
            {
                WindowWidth = value.Width,
                WindowHeight = value.Height,
            });

            OnPropertyChanged(nameof(WindowWidth));
            OnPropertyChanged(nameof(WindowHeight));
            ScheduleInstanceSave();
        }
    }

    private void SyncPresetFromInstance()
    {
        var item = SelectedInstance;
        _selectedPreset = item is null
            ? null
            : ResolutionPresets.FirstOrDefault(preset =>
                preset.Width == item.WindowWidth && preset.Height == item.WindowHeight);

        OnPropertyChanged(nameof(SelectedPreset));
    }

    /// <summary>
    /// 立刻落盘。
    ///
    /// 不做防抖定时器：文件只有几 KB，逐次写入的代价可以忽略；
    /// 而定时器会带来"改完立刻关窗口就丢设置"的问题，得不偿失。
    /// 并发写入用一个信号量串行化，且**写入时重新读取当前值**，
    /// 这样即使多次保存交错，最后落盘的一定是最新状态。
    /// </summary>
    private void ScheduleInstanceSave() => _ = SaveInstanceSettingsAsync();

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private async Task SaveInstanceSettingsAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            if (SelectedInstance is not { } item) return;

            var store = new JsonInstanceStore(_launcher);
            var catalog = new InstanceCatalog(await store.LoadAsync());

            if (catalog.UpdateSettings(item.Model.Id, item.Model.Settings) is null) return;
            await store.SaveAsync(catalog.Instances);

            Status = $"已保存：{item.Name} · {item.MemoryMb} MB · {item.WindowWidth}×{item.WindowHeight}";
        }
        catch (Exception ex)
        {
            Status = $"保存设置失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"保存实例设置失败 → {ex.GetBaseException().Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>供诊断/测试用：等待一次落盘完成（正常界面路径是即发即忘）。</summary>
    public Task SaveSettingsNowAsync() => SaveInstanceSettingsAsync();

    // ---- 页签与内容 ----

    public ObservableCollection<ContentTabItem> ContentTabs { get; }

    public ContentTabItem SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null) return;

            var previousKind = _selectedTab.ContentKind;
            if (!SetProperty(ref _selectedTab, value)) return;

            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(IsContentList));
            OnPropertyChanged(nameof(IsLogTab));
            OnPropertyChanged(nameof(IsModsTab));
            OnPropertyChanged(nameof(ShowOverview));
            OnPropertyChanged(nameof(ShowContentList));
            OnPropertyChanged(nameof(ShowLog));
            OnPropertyChanged(nameof(ContentEmptyHint));
            OnPropertyChanged(nameof(SearchPlaceholder));
            RaiseCommandStates();
            UpdateLogTimer();

            // 离开内容页签就把搜索面板收起来（结果保留，同一实例下仍有效）
            if (!IsContentList) IsSearchPanelExpanded = false;

            // 内容类型变了（模组 → 资源包）→ 搜索结果与待确认计划都不再适用
            if (previousKind != value.ContentKind)
            {
                ResetSearchState();

                // CurseForge 没有光影分类，切到光影时把来源切回 Modrinth
                if (value.ContentKind is { } tabKind && tabKind.CurseForgeClassId() is null)
                    SearchSourceIndex = 0;
            }

            if (IsLogTab) _ = RefreshLogAsync();
            else _ = RefreshContentAsync();
        }
    }

    public bool IsOverview => SelectedTab.Kind == DetailTabKind.Overview;

    public bool IsContentList => SelectedTab.Kind == DetailTabKind.Content;

    public bool IsLogTab => SelectedTab.Kind == DetailTabKind.Log;

    /// <summary>当前是否在模组页签（"检查更新"只对模组有意义）。</summary>
    public bool IsModsTab => SelectedTab.ContentKind == ContentKind.Mod;

    public bool ShowOverview => HasSelection && IsOverview;

    public bool ShowContentList => HasSelection && IsContentList;

    public bool ShowLog => HasSelection && IsLogTab;

    public ObservableCollection<ContentEntryItem> Content { get; } = new();

    public bool IsContentEmpty => Content.Count == 0;

    public string ContentEmptyHint => SelectedTab.ContentKind switch
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
    public ICommand OpenResourcePacksFolderCommand { get; }
    public ICommand OpenShaderPacksFolderCommand { get; }
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
        var kind = SelectedTab.ContentKind;

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
        UpdateCount = 0;   // 列表重建后旧的升级标记已失效
        foreach (var entry in entries)
            Content.Add(new ContentEntryItem(entry, ToggleAsync, DeleteAsync, UpgradeModAsync));

        OnPropertyChanged(nameof(IsContentEmpty));
        RaiseCommandStates();
    }

    /// <summary>刷新左侧实例行的数量、三个页签的数量，以及概览处目录入口的可见性。</summary>
    private async Task RefreshCountsAsync(InstanceListItem? instance)
    {
        instance?.RefreshContentCounts();
        RaiseFolderVisibilityChanged();

        foreach (var tab in ContentTabs)
        {
            if (tab.ContentKind is not { } kind || instance is null)
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
        if (SelectedInstance is null || SelectedTab.ContentKind is not { } kind) return;

        var wasEnabled = item.IsEnabled;
        var instance = SelectedInstance;

        try
        {
            var directory = instance.DirectoryFor(kind);
            var updated = await Task.Run(() => _contentManager.Toggle(item.Entry, directory));

            // 就地更新这一行：绝不重建集合，否则 ListBox 会丢失选中项（高亮乱跳）
            item.ApplyEntry(updated);
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
        if (SelectedInstance is null || SelectedTab.ContentKind is not { } kind) return;

        var instance = SelectedInstance;

        try
        {
            var directory = instance.DirectoryFor(kind);
            var movedTo = await Task.Run(() => _contentManager.Delete(item.Entry, directory));

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
        if (SelectedInstance is null || SelectedTab.ContentKind is not { } kind) return;

        var instance = SelectedInstance;
        var directory = instance.DirectoryFor(kind);
        var targets = Content.Where(item => item.IsEnabled != enable).ToList();
        var changed = 0;

        try
        {
            foreach (var item in targets)
            {
                var updated = await Task.Run(() => _contentManager.Toggle(item.Entry, directory));
                item.ApplyEntry(updated);
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

    // ---- 日志 ----

    private string _logText = "";
    private string _logPathText = "尚未读取日志";

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string LogPathText
    {
        get => _logPathText;
        private set => SetProperty(ref _logPathText, value);
    }

    public ICommand RefreshLogCommand { get; }

    /// <summary>
    /// 读取 logs/latest.log 的尾部。
    ///
    /// 为什么不读进程 stdout：客户端配置的 log4j（client-1.12.xml）会把日志按 XML 写到 stdout，
    /// 人读不了；而 latest.log 是纯文本，且游戏运行中会持续追加。
    /// 打开时必须带 FileShare.ReadWrite —— 游戏正在写这个文件。
    /// </summary>
    public async Task RefreshLogAsync()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            LogPathText = "尚未选中实例";
            LogText = "";
            return;
        }

        var path = Path.Combine(instance.GameDirectory, "logs", "latest.log");
        LogPathText = path;

        try
        {
            if (!File.Exists(path))
            {
                LogText = $"还没有日志文件。启动一次游戏后这里会出现内容。{Environment.NewLine}{Environment.NewLine}预期位置：{path}";
                return;
            }

            var text = await Task.Run(() => ReadTail(path, 128 * 1024));
            if (text != LogText) LogText = text;
        }
        catch (Exception ex)
        {
            LogText = $"读取日志失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"读取日志失败 {path} → {ex.GetBaseException().Message}");
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();

        // 从中间开始读时首行可能是半截，丢掉
        if (start > 0)
        {
            var index = text.IndexOf('\n');
            if (index >= 0) text = text[(index + 1)..];
        }

        return text;
    }

    private void UpdateLogTimer()
    {
        if (IsLogTab && SelectedInstance is not null) _logTimer.Start();
        else _logTimer.Stop();
    }

    // ---- 模组升级 ----

    private bool _isCheckingUpdates;
    private int _updateCount;

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set
        {
            if (!SetProperty(ref _isCheckingUpdates, value)) return;
            OnPropertyChanged(nameof(CheckUpdatesText));
            RaiseCommandStates();
        }
    }

    public int UpdateCount
    {
        get => _updateCount;
        private set
        {
            if (!SetProperty(ref _updateCount, value)) return;
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(UpdateSummaryText));
        }
    }

    public bool HasUpdates => UpdateCount > 0;

    public string UpdateSummaryText => UpdateCount > 0 ? $"{UpdateCount} 个可升级" : "";

    public string CheckUpdatesText => IsCheckingUpdates ? "检查中…" : "检查更新";

    public ICommand CheckUpdatesCommand { get; }

    private bool CanCheckUpdates =>
        SelectedTab.ContentKind == ContentKind.Mod
        && SelectedInstance is not null
        && Content.Count > 0
        && !IsCheckingUpdates;

    /// <summary>
    /// 检查 mods 目录里的模组是否有新版本。
    ///
    /// 流程：本地 SHA1 → 批量反查 Modrinth → 按实例的游戏版本与加载器取版本列表
    /// → 判定 → 把结果就地贴到对应的行上（不重建列表）。
    /// </summary>
    private async Task CheckModUpdatesAsync()
    {
        if (SelectedInstance is not { } instance || SelectedTab.ContentKind != ContentKind.Mod) return;

        IsCheckingUpdates = true;
        try
        {
            var items = Content.ToList();
            if (items.Count == 0)
            {
                Status = "mods 目录里没有文件";
                return;
            }

            Status = $"正在检查 {items.Count} 个模组（Modrinth{( _updateService.CurseForgeEnabled ? " + CurseForge" : "") }）…";

            // 加载器：不按加载器过滤会把 Fabric 版本推给 Forge 整合包
            IReadOnlyList<string> loaders = Array.Empty<string>();
            var version = await GameLauncher.TryLoadInstalledVersionAsync(_launcher, instance.VersionId);
            if (version is not null) loaders = LoaderDetector.Detect(version);

            var report = await _updateService.CheckAsync(
                items.Select(item => item.Entry.Path).ToList(),
                instance.VersionId,
                loaders);

            // 就地贴到行上，不重建列表（升级提示 + 识别来源）
            var byPath = report.LocalMods.ToDictionary(local => local.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                item.SetUpdate(report.Updates.FirstOrDefault(update => update.LocalPath == item.Entry.Path));
                item.SetIdentification(
                    byPath.TryGetValue(item.Entry.Path, out var local) ? local.Matched?.Source : null,
                    byPath.TryGetValue(item.Entry.Path, out var matchedLocal) ? matchedLocal.Matched?.Channel : null);
            }

            UpdateCount = report.Updates.Count;
            Status = DescribeReport(items.Count, report);

            LauncherLog.Write(_launcher.Root,
                $"检查更新 {instance.Id}：文件 {items.Count}，Modrinth {report.ModrinthMatches}，"
                + $"CurseForge {report.CurseForgeMatches}，可升级 {report.Updates.Count}，"
                + $"加载器 [{(loaders.Count == 0 ? "原版" : string.Join(",", loaders))}]"
                + (report.CurseForgeNote is { } note ? $"，CF 说明 {note}" : ""));
        }
        catch (Exception ex)
        {
            Status = $"检查更新失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"检查更新失败 {instance.Id} → {ex.GetBaseException().Message}");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    /// <summary>供诊断用：直接触发一次更新检查并等待完成。</summary>
    public Task CheckUpdatesAsync() => CheckModUpdatesAsync();

    /// <summary>
    /// 把检查结果组织成一句人能读的话。
    ///
    /// 三个数字必须分开说清楚："已识别"（平台能认出这是哪个项目的哪个版本）、
    /// "可升级"、以及"未识别"（两个平台都查不到，通常是自制或未上传的模组）。
    /// 早先只报"都已是当前版本"，会把"认不出来"错说成"已是最新"。
    /// </summary>
    private static string DescribeReport(int fileCount, ModUpdateReport report)
    {
        // 状态栏只有一行，用 · 分隔并把"已识别"的两个来源压在括号里，尽量不触发省略号
        var text = $"更新检查：{fileCount} 个文件 · {report.Updates.Count} 可升级 · "
                 + $"{report.MatchedCount} 已识别（Modrinth {report.ModrinthMatches} / CurseForge {report.CurseForgeMatches}）";

        if (report.UnmatchedCount > 0)
            text += $" · {report.UnmatchedCount} 无法识别";

        if (report.CurseForgeNote is { } problem)
            text += $" · CF 不可用（{problem}）";

        return text;
    }

    /// <summary>
    /// 升级单个模组：下载新版 → 旧版移入 .trash → 刷新列表。
    /// 注意新版文件名通常与旧版不同（版本号在文件名里），所以是"新增 + 移除旧"，
    /// 不是覆盖。下载失败时不会动任何现有文件。
    /// </summary>
    private async Task UpgradeModAsync(ContentEntryItem item)
    {
        if (SelectedInstance is not { } instance || item.PendingUpdate is not { } update) return;
        if (string.IsNullOrWhiteSpace(update.Latest.DownloadUrl)) return;

        var modsDirectory = instance.ModsDirectory;
        var destination = Path.Combine(modsDirectory, update.Latest.FileName);

        try
        {
            Status = $"正在下载 {update.Latest.FileName}…";

            // CurseForge 通常没有 SHA1，此时只能靠下载器自身的一致性（大小 + 原子落盘）
            var sha1 = string.IsNullOrWhiteSpace(update.Latest.Sha1) ? null : update.Latest.Sha1;
            await _downloader.EnsureFromAsync(new[] { update.Latest.DownloadUrl }, destination, sha1);

            // 下载成功之后才动旧文件
            var movedTo = _contentManager.Delete(item.Entry, modsDirectory);

            Status = $"已升级 {item.Name}：{update.Current.VersionLabel} → {update.Latest.VersionLabel}"
                   + $"（旧版在 {ContentScanner.TrashDirectoryName}\\{Path.GetFileName(movedTo)}）";

            LauncherLog.Write(_launcher.Root,
                $"升级 {item.Entry.FileName} → {update.Latest.FileName}（{update.Current.VersionLabel} → {update.Latest.VersionLabel}，来源 {update.Source}）");
        }
        catch (Exception ex)
        {
            Status = $"升级失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"升级失败 {item.Entry.FileName} → {ex.GetBaseException().Message}");
        }
        finally
        {
            // 文件集合变了（新增一个新版本、移走一个旧版本），整表刷新
            await RefreshContentAsync();
        }
    }

    // ---- 搜索与安装 ----

    private string _searchQuery = "";
    private string _searchSummaryText = "";
    private bool _isSearching;
    private bool _isPlanVisible;
    private bool _isInstalling;
    private InstallPlan? _pendingPlan;
    private string? _pendingDirectory;
    private int _searchSourceIndex;
    private bool _suppressVersionReload;
    private ModSearchResultItem? _pendingItem;
    private InstallVersionChoice? _selectedInstallVersion;
    private IReadOnlyList<string> _pendingLoaders = Array.Empty<string>();
    private ContentKind _pendingKind = ContentKind.Mod;

    /// <summary>该项目在当前游戏版本下可安装的所有版本。</summary>
    public ObservableCollection<InstallVersionChoice> InstallVersions { get; } = new();

    public bool HasInstallVersions => InstallVersions.Count > 0;

    /// <summary>切换版本会重新解析依赖并刷新计划。</summary>
    public InstallVersionChoice? SelectedInstallVersion
    {
        get => _selectedInstallVersion;
        set
        {
            if (!SetProperty(ref _selectedInstallVersion, value)) return;
            if (_suppressVersionReload || value is null) return;

            _ = ResolvePlanForVersionAsync(value);
        }
    }

    /// <summary>搜索来源：0 = Modrinth，1 = CurseForge。</summary>
    public int SearchSourceIndex
    {
        get => _searchSourceIndex;
        set
        {
            if (!SetProperty(ref _searchSourceIndex, value)) return;
            OnPropertyChanged(nameof(SelectedSearchSource));
        }
    }

    public UpdateSource SelectedSearchSource =>
        _searchSourceIndex == 1 ? UpdateSource.CurseForge : UpdateSource.Modrinth;

    /// <summary>未配置 key 时 CurseForge 来源不可用，界面上据此禁用选项。</summary>
    public bool IsCurseForgeSearchAvailable => _curseForge is not null;

    public ObservableCollection<ModSearchResultItem> SearchResults { get; } = new();
    public ObservableCollection<InstallPlanLineItem> PlanLines { get; } = new();

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value)) return;
            (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SearchSummaryText
    {
        get => _searchSummaryText;
        private set
        {
            if (!SetProperty(ref _searchSummaryText, value)) return;
            OnPropertyChanged(nameof(HasSearchSummary));
        }
    }

    public bool HasSearchSummary => _searchSummaryText.Length > 0;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            OnPropertyChanged(nameof(SearchButtonText));
            (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SearchButtonText => _isSearching ? "搜索中…" : "搜索";

    public bool IsPlanVisible
    {
        get => _isPlanVisible;
        private set => SetProperty(ref _isPlanVisible, value);
    }

    private bool _isSearchPanelExpanded = true;

    /// <summary>搜索面板是否展开。收起后只留搜索框，把高度还给内容列表。</summary>
    public bool IsSearchPanelExpanded
    {
        get => _isSearchPanelExpanded;
        private set
        {
            if (!SetProperty(ref _isSearchPanelExpanded, value)) return;
            OnPropertyChanged(nameof(SearchPanelToggleText));
        }
    }

    /// <summary>收起时把结果条数写进按钮，否则用户不知道里面还有东西。</summary>
    public string SearchPanelToggleText => _isSearchPanelExpanded
        ? "收起"
        : SearchResults.Count > 0 ? $"展开（{SearchResults.Count} 条）" : "展开";

    /// <summary>搜索框的占位提示随当前页签变化（模组 / 资源包 / 光影）。</summary>
    public string SearchPlaceholder => SelectedTab.ContentKind is { } kind
        ? $"搜索{kind.DisplayName()}…"
        : "搜索…";

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (!SetProperty(ref _isInstalling, value)) return;
            OnPropertyChanged(nameof(InstallButtonText));
            (ConfirmInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string InstallButtonText => _isInstalling ? "安装中…" : "确认安装";

    public ICommand SearchCommand { get; }
    public ICommand ConfirmInstallCommand { get; }
    public ICommand CancelPlanCommand { get; }
    public ICommand ToggleSearchPanelCommand { get; }

    /// <summary>
    /// 清空搜索结果与待确认的安装计划。
    ///
    /// 只在**切换实例**时调用。同一个实例内切页签（模组 → 资源包 → 模组）不清，
    /// 否则每次来回都要重搜一遍，很烦。
    /// 查询词保留，方便直接再搜一次。
    /// </summary>
    private void ResetSearchState()
    {
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchPanelToggleText));

        // 连搜索词一起清掉：切换内容类型后旧关键词已经没有意义，
        // 留着会让人以为"这个搜索在新页签里也生效"。
        SearchQuery = "";

        SearchSummaryText = "";

        _pendingPlan = null;
        _pendingItem = null;
        PlanLines.Clear();
        InstallVersions.Clear();
        OnPropertyChanged(nameof(HasInstallVersions));
        IsPlanVisible = false;
        (ConfirmInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool CanSearch => !IsSearching && SelectedInstance is not null && _searchQuery.Trim().Length > 0;

    private bool CanConfirmInstall => !IsInstalling && _pendingPlan is not null;

    /// <summary>在 Modrinth 搜索模组，按当前实例的游戏版本与加载器过滤。</summary>
    private async Task SearchModsAsync()
    {
        if (SelectedInstance is not { } instance) return;

        var query = _searchQuery.Trim();
        if (query.Length == 0) return;

        IsSearching = true;
        try
        {
            // 日志页签没有内容类型 —— 那里不显示搜索面板，走到这里直接退出
            if (SelectedTab.ContentKind is not { } kind) return;

            // 只有模组按加载器过滤。资源包/光影的 loaders 是 "minecraft"，
            // 按 fabric/forge 过滤会一条都搜不到。
            var loaders = kind.UsesLoaderFilter()
                ? await DetectLoadersAsync(instance)
                : Array.Empty<string>();

            var source = SelectedSearchSource;

            if (source == UpdateSource.CurseForge && !IsCurseForgeSearchAvailable)
            {
                SearchSummaryText = "CurseForge 搜索需要配置 CURSEFORGE_API_KEY";
                Status = SearchSummaryText;
                return;
            }

            if (source == UpdateSource.CurseForge && kind.CurseForgeClassId() is not { } classId)
            {
                SearchSummaryText = $"CurseForge 没有{kind.DisplayName()}分类，请把来源切回 Modrinth";
                Status = SearchSummaryText;
                return;
            }

            Status = $"正在 {source} 搜索「{query}」…";

            SearchResults.Clear();

            if (source == UpdateSource.CurseForge)
            {
                var hits = await _curseForge!.SearchAsync(
                    query, instance.VersionId, loaders, 8, kind.CurseForgeClassId()!.Value);

                foreach (var hit in hits)
                {
                    SearchResults.Add(new ModSearchResultItem(
                        UpdateSource.CurseForge,
                        hit.ModId.ToString(),
                        hit.DisplayName,
                        hit.Slug,
                        hit.DownloadCount,
                        BeginInstallAsync));
                }
            }
            else
            {
                var hits = await _modrinth.SearchAsync(
                    query, instance.VersionId, loaders, 8, kind.ModrinthProjectType());

                foreach (var hit in hits)
                {
                    SearchResults.Add(new ModSearchResultItem(
                        UpdateSource.Modrinth,
                        hit.Slug,
                        hit.DisplayName,
                        hit.Slug,
                        hit.Downloads,
                        BeginInstallAsync));
                }
            }

            OnPropertyChanged(nameof(HasSearchResults));

            // 搜出结果就展开 —— 收起状态下搜完看不到东西会让人以为没搜到
            IsSearchPanelExpanded = true;

            SearchSummaryText = SearchResults.Count == 0
                ? $"{source} 上没有找到适用于 {instance.VersionId} 的{kind.DisplayName()}"
                : $"找到 {SearchResults.Count} 个{kind.DisplayName()}"
                  + $"（{source} · {instance.VersionId}"
                  + (loaders.Count > 0 ? " / " + string.Join(",", loaders) : "") + "）";

            Status = SearchSummaryText;
        }
        catch (Exception ex)
        {
            SearchSummaryText = $"搜索失败：{ex.GetBaseException().Message}";
            Status = SearchSummaryText;
            LauncherLog.Write(_launcher.Root, $"搜索失败 {query} → {ex.GetBaseException().Message}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>解析依赖并展示安装计划，等用户确认后才真正下载。</summary>
    private async Task BeginInstallAsync(ModSearchResultItem item)
    {
        if (SelectedInstance is not { } instance) return;
        if (SelectedTab.ContentKind is not { } kind) return;

        try
        {
            var loaders = kind.UsesLoaderFilter()
                ? await DetectLoadersAsync(instance)
                : Array.Empty<string>();

            Status = $"正在查询 {item.Title} 的可用版本…";

            var choices = await LoadVersionChoicesAsync(item, instance.VersionId, loaders);

            if (choices.Count == 0)
            {
                Status = $"{item.Title} 没有适用于 {instance.VersionId} 的版本";
                return;
            }

            _pendingItem = item;
            _pendingKind = kind;
            _pendingLoaders = loaders;
            _pendingDirectory = Path.Combine(instance.GameDirectory, kind.DirectoryName());

            // 填充下拉时不要触发"选中即重新解析"，否则会解析两遍
            _suppressVersionReload = true;

            InstallVersions.Clear();
            foreach (var choice in choices) InstallVersions.Add(choice);

            _selectedInstallVersion = choices[0];
            OnPropertyChanged(nameof(SelectedInstallVersion));
            OnPropertyChanged(nameof(HasInstallVersions));

            _suppressVersionReload = false;

            await ResolvePlanForVersionAsync(choices[0]);
        }
        catch (Exception ex)
        {
            Status = $"解析失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"解析安装计划失败 {item.ProjectRef} → {ex.GetBaseException().Message}");
        }
    }

    /// <summary>列出该项目在当前游戏版本下的所有可安装版本（含预发布，标出来）。</summary>
    private async Task<IReadOnlyList<InstallVersionChoice>> LoadVersionChoicesAsync(
        ModSearchResultItem item,
        string gameVersion,
        IReadOnlyList<string> loaders)
    {
        if (item.Source == UpdateSource.CurseForge)
        {
            if (_curseForge is null || !int.TryParse(item.ProjectRef, out var modId))
                return Array.Empty<InstallVersionChoice>();

            var files = await _curseForge.GetFilesAsync(modId, gameVersion, loaders);

            return files
                .Select(file => new InstallVersionChoice(
                    file.FileId.ToString(),
                    string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
                    ReleaseChannelExtensions.FromCurseForgeReleaseType(file.ReleaseType)))
                .ToList();
        }

        var versions = await _modrinth.GetProjectVersionsAsync(item.ProjectRef, gameVersion, loaders);

        return versions
            .Select(version => new InstallVersionChoice(
                version.Id,
                version.VersionNumber,
                ReleaseChannelExtensions.FromModrinthType(version.VersionType)))
            .ToList();
    }

    /// <summary>按指定版本重新解析计划（包括该版本声明的依赖）。</summary>
    private async Task ResolvePlanForVersionAsync(InstallVersionChoice choice)
    {
        if (_pendingItem is not { } item || SelectedInstance is not { } instance) return;

        try
        {
            Status = $"正在解析 {item.Title} {choice.Label} 的依赖…";

            InstallPlan plan;

            if (item.Source == UpdateSource.CurseForge)
            {
                if (_curseForgeInstaller is null || !int.TryParse(item.ProjectRef, out var modId))
                {
                    Status = "CurseForge 不可用（未配置 CURSEFORGE_API_KEY）";
                    return;
                }

                plan = await _curseForgeInstaller.ResolveAsync(
                    modId,
                    instance.VersionId,
                    _pendingLoaders,
                    includeDependencies: _pendingKind.HasDependencies(),
                    rootTitle: item.Title,
                    fileId: int.TryParse(choice.Ref, out var fileId) ? fileId : null);
            }
            else
            {
                plan = await _installer.ResolveAsync(
                    item.ProjectRef,
                    instance.VersionId,
                    _pendingLoaders,
                    versionId: choice.Ref);
            }

            _pendingPlan = plan;

            PlanLines.Clear();
            foreach (var target in plan.Main) PlanLines.Add(new InstallPlanLineItem(target));
            foreach (var target in plan.Dependencies) PlanLines.Add(new InstallPlanLineItem(target));

            IsPlanVisible = true;
            IsSearchPanelExpanded = true;
            (ConfirmInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            Status = $"待确认：{item.Title} {choice.Label} 共 {plan.Targets.Count} 个文件";
        }
        catch (Exception ex)
        {
            Status = $"解析失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root,
                $"解析安装计划失败 {item.ProjectRef} {choice.Ref} → {ex.GetBaseException().Message}");
        }
    }

    private async Task ConfirmInstallAsync()
    {
        if (_pendingPlan is not { } plan || SelectedInstance is not { } instance) return;

        IsInstalling = true;
        try
        {
            Status = "正在下载…";
            var outcome = await _installer.InstallAsync(
                plan, _pendingDirectory ?? instance.ModsDirectory);

            Status = $"安装完成：新增 {outcome.Installed.Count} 个，跳过 {outcome.Skipped.Count} 个"
                   + (outcome.Failed.Count > 0 ? $"，失败 {outcome.Failed.Count} 个" : "");

            LauncherLog.Write(_launcher.Root,
                $"安装 {string.Join(",", plan.Targets.Select(target => target.FileName))}"
                + $" → 新增 {outcome.Installed.Count}，跳过 {outcome.Skipped.Count}，失败 {outcome.Failed.Count}");

            IsPlanVisible = false;
            _pendingPlan = null;
            PlanLines.Clear();
            SearchResults.Clear();
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(SearchPanelToggleText));
            SearchSummaryText = "";

            await RefreshContentAsync();
            await RefreshCountsAsync(instance);
        }
        catch (Exception ex)
        {
            Status = $"安装失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"安装失败 → {ex.GetBaseException().Message}");
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private void CancelPlan()
    {
        _pendingPlan = null;
        _pendingItem = null;
        PlanLines.Clear();
        InstallVersions.Clear();
        OnPropertyChanged(nameof(HasInstallVersions));
        IsPlanVisible = false;
        (ConfirmInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        Status = "已取消安装";
    }

    /// <summary>供诊断用：直接触发一次搜索并等待完成。</summary>
    public Task SearchForDiagnosticsAsync(string query)
    {
        SearchQuery = query;
        return SearchModsAsync();
    }

    /// <summary>供诊断用：解析某个搜索结果的依赖计划（不下载）。</summary>
    public Task PrepareInstallForDiagnosticsAsync(ModSearchResultItem item) => BeginInstallAsync(item);

    /// <summary>供诊断用：选中第 N 个可用版本并等待计划重新解析完成。</summary>
    public async Task SelectInstallVersionForDiagnosticsAsync(int index)
    {
        if (index < 0 || index >= InstallVersions.Count) return;

        var choice = InstallVersions[index];

        // 走与界面相同的路径，但不让属性回调再触发一次解析
        _suppressVersionReload = true;
        _selectedInstallVersion = choice;
        OnPropertyChanged(nameof(SelectedInstallVersion));
        _suppressVersionReload = false;

        await ResolvePlanForVersionAsync(choice);
    }

    /// <summary>从已安装的版本 JSON 推断加载器；判断不出来时返回空（不按加载器过滤）。</summary>
    private async Task<IReadOnlyList<string>> DetectLoadersAsync(InstanceListItem instance)
    {
        var version = await GameLauncher.TryLoadInstalledVersionAsync(_launcher, instance.VersionId);
        return version is null ? Array.Empty<string>() : LoaderDetector.Detect(version);
    }

    // ---- 打开目录 ----

    /// <summary>由窗口注入的剪贴板写入器。全部打开方式都失败时，把路径复制给用户兜底。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    private void OnOpenError(Exception ex) => Status = $"打开目录失败：{ex.GetBaseException().Message}";

    private Task OpenGameDirectory()
        => SelectedInstance is null ? Task.CompletedTask : OpenDirectoryAsync(SelectedInstance.GameDirectory);

    private Task OpenModsFolder()
        => SelectedInstance is null ? Task.CompletedTask : OpenDirectoryAsync(SelectedInstance.ModsDirectory);

    private Task OpenResourcePacksFolder()
        => SelectedInstance is null ? Task.CompletedTask : OpenDirectoryAsync(SelectedInstance.ResourcePacksDirectory);

    private Task OpenShaderPacksFolder()
        => SelectedInstance is null ? Task.CompletedTask : OpenDirectoryAsync(SelectedInstance.ShaderPacksDirectory);

    private Task OpenContentFolder()
        => SelectedInstance is null || SelectedTab.ContentKind is not { } kind
            ? Task.CompletedTask
            : OpenDirectoryAsync(SelectedInstance.DirectoryFor(kind));

    private async Task OpenDirectoryAsync(string directory)
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

        // Low 完整性下这几种方式全都会被系统拒绝，直接给出可执行的建议，
        // 而不是把三条失败信息堆在状态栏里让用户猜。
        if (ProcessContext.IsLowIntegrity)
        {
            var reason = "当前进程处于受限模式（Low 完整性），系统不允许它调起资源管理器。"
                       + "请改用仓库根目录的 launch.cmd 启动（通过 dotnet 宿主运行，可获得 Medium 权限）；"
                       + "路径已复制到剪贴板，也可直接粘贴到资源管理器地址栏。";

            LauncherLog.Write(_launcher.Root,
                $"打开目录被跳过（{ProcessContext.Describe()}）：{target}");

            if (CopyToClipboard is not null)
            {
                try { await CopyToClipboard(target); } catch { /* 剪贴板不可用就算了 */ }
            }

            Status = $"{reason}  路径 {target}";
            return;
        }

        // 依次尝试 shell32（交给已有外壳）、shell；两者都不会弹窗。详见 FolderLauncher
        var attempts = await Interop.FolderLauncher.OpenAsync(target);

        var report = string.Join(" | ", attempts.Select(a => $"{a.Strategy}={(a.Success ? "OK" : a.Error)}"));
        LauncherLog.Write(_launcher.Root,
            $"打开目录 {target} → {report}   [权限 {ProcessContext.Describe()}]");

        if (attempts[^1].Success)
        {
            Status = $"已打开 {target}";
            return;
        }

        var detail = string.Join("；", attempts.Select(attempt => $"{attempt.Strategy}=({attempt.Error})"));

        // 全部失败时至少把路径交给用户：粘到资源管理器地址栏就能打开
        if (CopyToClipboard is not null)
        {
            try
            {
                await CopyToClipboard(target);
                Status = $"无法调起资源管理器（{detail}）。路径已复制到剪贴板，粘贴到地址栏即可打开：{target}";
                return;
            }
            catch
            {
                // 剪贴板也不可用就走下面的纯报告
            }
        }

        Status = $"打开目录失败：{detail}；路径 {target}";
    }

    private void RaiseFolderVisibilityChanged()
    {
        OnPropertyChanged(nameof(HasModsDirectory));
        OnPropertyChanged(nameof(HasResourcePacksDirectory));
        OnPropertyChanged(nameof(HasShaderPacksDirectory));
    }

    private void RaiseCommandStates()
    {
        (LaunchGameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CheckUpdatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshLogCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenGameFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenModsFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenResourcePacksFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenShaderPacksFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenContentFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisableAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EnableAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
