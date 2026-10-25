using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Diagnostics;
using NPMC.Launcher.Infrastructure.Hashing;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Launch;
using NPMC.Launcher.Infrastructure.Minecraft;
using NPMC.Launcher.Infrastructure.Modrinth;
using NPMC.Launcher.Infrastructure.Mods;
using NPMC.Launcher.Infrastructure.Modpacks;
using NPMC.Launcher.Infrastructure.Runtime;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Storage;
using NPMC.Launcher.Infrastructure.Translation;

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
    private readonly IconLoader _icons;
    private readonly ContentMetadataCache _contentMetadata;
    private readonly HashSet<string> _autoIdentifiedInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly TranslationClient _translation;
    private string? _descriptionLanguage;
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
        _icons = new IconLoader(
            _http,
            Path.Combine(_launcher.Root, "cache", "icons"),
            message => LauncherLog.Write(_launcher.Root, message));

        _contentMetadata = new ContentMetadataCache(Path.Combine(_launcher.Root, "cache"));
        _translation = new TranslationClient(_http, Path.Combine(_launcher.Root, "cache", "translations"));

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

        ShowInstancesCommand = new RelayCommand(() => Navigate(NavPage.Instances));
        ShowDownloadsCommand = new RelayCommand(() => Navigate(NavPage.Downloads));
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanDownload);
        OpenDetailInBrowserCommand = new RelayCommand(OpenDetailInBrowser);

        ShowVersionsModeCommand = new RelayCommand(() => SetDownloadMode(DownloadMode.Versions));
        ShowModpacksModeCommand = new RelayCommand(() => SetDownloadMode(DownloadMode.Modpacks));
        InstallModpackCommand = new AsyncRelayCommand(
            InstallModpackAsync,
            () => HasModpackPlan && !IsInstallingModpack);

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

            // 换实例 → 重新列出可用的 Java（概览页那一行要用）
            _ = LoadJavaChoicesAsync(value);
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

    private string _gameVersion = "";

    /// <summary>
    /// 真正用于查询模组平台的游戏版本号。
    /// 加载器实例的 VersionId 是 fabric-loader-0.20.1-1.20.1，而平台只认 1.20.1。
    /// </summary>
    public string GameVersionText => _gameVersion;

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
        _gameVersion = await ResolveGameVersionAsync(instance);
        OnPropertyChanged(nameof(GameVersionText));

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

            // 不可用的页签点了也不进去：把选中项推回原值，并说明原因
            if (!value.IsEnabled)
            {
                OnPropertyChanged(nameof(SelectedTab));
                Status = value.TabTip ?? "该页签当前不可用";
                return;
            }

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

        // 徽标/R·B·A/图标都不依赖"检查更新"：先用缓存，再后台识别一次。
        // 资源包与光影同样适用 —— 它们也能按哈希被认领，只是不按加载器过滤。
        if (Content.Count > 0)
            _ = LoadContentMetadataAsync(instance, contentKind, Content.ToList());
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

            // 查询要用**游戏版本号**（1.20.1），不是加载器 profile 名（fabric-loader-...）
            var gameVersion = await ResolveGameVersionAsync(instance);

            // 加载器：不按加载器过滤会把 Fabric 版本推给 Forge 整合包
            IReadOnlyList<string> loaders = Array.Empty<string>();
            var version = await GameLauncher.TryLoadInstalledVersionAsync(_launcher, instance.VersionId);
            if (version is not null) loaders = LoaderDetector.Detect(version);

            var report = await _updateService.CheckAsync(
                items.Select(item => item.Entry.Path).ToList(),
                gameVersion,
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

            // 图标后补：不影响列表出现
            _ = SaveMetadataAsync(items, report);

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
    private string _pendingGameVersion = "";

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

            // 空查询时按钮文案变成"热门"
            OnPropertyChanged(nameof(SearchButtonText));
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

    public string SearchButtonText => _isSearching
        ? "搜索中…"
        : _searchQuery.Trim().Length == 0 ? "热门" : "搜索";

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

    /// <summary>搜索框的占位提示随当前页签变化；查询为空时按钮会去取热门。</summary>
    public string SearchPlaceholder => SelectedTab.ContentKind is { } kind
        ? $"搜索{kind.DisplayName()}…（留空则看热门）"
        : "搜索…（留空则看热门）";

    // ---- 搜索结果详情 ----

    private ModSearchResultItem? _selectedSearchResult;
    private bool _isDetailVisible;
    private string _detailTitle = "";
    private string _detailSubtitle = "";
    private string _detailBody = "";
    private string _detailUrl = "";

    /// <summary>正文摘要缓存：同一项目反复点开不重复请求。</summary>
    private readonly Dictionary<string, string> _detailBodyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 选中一条搜索结果 → 拉取并显示详情。
    ///
    /// 为什么要有这个：同类模组的名字经常只差一两个字（Sodium / Sodium Extra /
    /// Sodium Options API…），只有一行摘要根本分不清该装哪个。
    /// </summary>
    public ModSearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (!SetProperty(ref _selectedSearchResult, value)) return;
            if (value is not null) _ = LoadSearchDetailAsync(value);
        }
    }

    public bool IsDetailVisible
    {
        get => _isDetailVisible;
        private set => SetProperty(ref _isDetailVisible, value);
    }

    public string DetailTitle
    {
        get => _detailTitle;
        private set => SetProperty(ref _detailTitle, value);
    }

    public string DetailSubtitle
    {
        get => _detailSubtitle;
        private set => SetProperty(ref _detailSubtitle, value);
    }

    public string DetailBody
    {
        get => _detailBody;
        private set => SetProperty(ref _detailBody, value);
    }

    private string _detailTranslation = "";

    /// <summary>简介的机器翻译。为空表示"不需要 / 翻不出来"，界面据此隐藏这一块。</summary>
    public string DetailTranslation
    {
        get => _detailTranslation;
        private set
        {
            if (!SetProperty(ref _detailTranslation, value)) return;
            OnPropertyChanged(nameof(HasDetailTranslation));
        }
    }

    public bool HasDetailTranslation => _detailTranslation.Length > 0;

    public string DetailUrl
    {
        get => _detailUrl;
        private set => SetProperty(ref _detailUrl, value);
    }

    public ICommand OpenDetailInBrowserCommand { get; }

    /// <summary>详情面板里的项目图标。</summary>
    private Bitmap? _detailIcon;

    public Bitmap? DetailIcon
    {
        get => _detailIcon;
        private set => SetProperty(ref _detailIcon, value);
    }

    /// <summary>
    /// 把简介翻成用户的语言（默认简体中文）。
    ///
    /// 三个"不做"的判据：
    ///   - 正文已经是中文 → 不必翻
    ///   - 目标语言是英文 → 原文就是英文，不必翻
    ///   - 翻译端点失败 → 只显示原文（翻译是附加信息，不是必需品）
    /// </summary>
    private async Task TranslateDetailAsync(int token, string body)
    {
        DetailTranslation = "";

        if (string.IsNullOrWhiteSpace(body)) return;
        if (LooksChinese(body)) return;

        var language = await DescriptionLanguageAsync();
        if (string.IsNullOrWhiteSpace(language)) return;
        if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return;

        var translated = await _translation.TranslateAsync(body, language);

        // 关键：用户在等待期间可能已经点了别的项目，A 的译文不能写到 B 上
        if (token != _detailToken) return;

        if (!string.IsNullOrWhiteSpace(translated)) DetailTranslation = translated;
    }

    /// <summary>目标语言来自全局设置（暂时没有设置页，默认 zh-CN）。</summary>
    private async Task<string> DescriptionLanguageAsync()
    {
        if (_descriptionLanguage is not null) return _descriptionLanguage;

        try
        {
            _descriptionLanguage = (await new JsonSettingsStore(_launcher).LoadAsync()).Language;
        }
        catch
        {
            _descriptionLanguage = "zh-CN";
        }

        if (string.IsNullOrWhiteSpace(_descriptionLanguage)) _descriptionLanguage = "zh-CN";
        return _descriptionLanguage;
    }

    /// <summary>粗略判断是否中文：汉字占比超过 5% 就算（避免把个别符号当文本）。</summary>
    private static bool LooksChinese(string text)
    {
        var han = 0;
        foreach (var character in text)
            if (character is >= '\u4E00' and <= '\u9FFF') han++;

        return han > text.Length / 20;
    }

    /// <summary>
    /// 内容列表的图标（打开页签就显示，**不需要点"检查更新"**）。
    ///
    /// 分两步：
    ///   1. 用记住的"文件名 → 图标地址"直接取图标（位图已在磁盘缓存里，零网络）
    ///   2. 该实例的该页签首次打开时，后台识别一次（只认领项目，不查版本），
    ///      补上徽标并记住图标地址
    ///
    /// 模组、资源包、光影用的是同一套逻辑：哈希认领与图标都与内容类型无关，
    /// 唯一的差别是**只有模组按加载器过滤**。
    /// </summary>
    private async Task LoadContentMetadataAsync(
        InstanceListItem instance,
        ContentKind kind,
        IReadOnlyList<ContentEntryItem> items)
    {
        // 1) 先用记住的结果：徽标、R/B/A、图标全部离线可用
        foreach (var item in items)
        {
            if (!_contentMetadata.TryGet(item.Entry.FileName, out var remembered)) continue;

            if (remembered.Identified)
                item.SetIdentification(remembered.Source, remembered.Channel);

            if (string.IsNullOrWhiteSpace(remembered.IconUrl)) continue;

            var icon = await _icons.GetAsync(remembered.IconUrl);
            if (icon is not null) item.Icon = icon;
        }

        // 2) 该实例的该页签首次打开时，后台识别一次（只认领，不查版本）
        if (!_autoIdentifiedInstances.Add($"{instance.Id}:{kind}")) return;

        try
        {
            var loaders = kind.UsesLoaderFilter()
                ? await DetectLoadersAsync(instance)
                : Array.Empty<string>();

            var gameVersion = await ResolveGameVersionAsync(instance);

            var report = await _updateService.CheckAsync(
                items.Select(item => item.Entry.Path).ToList(),
                gameVersion,
                loaders,
                includeProjectVersions: false);

            var byPath = report.LocalMods.ToDictionary(local => local.Path, StringComparer.OrdinalIgnoreCase);

            var projectIds = report.LocalMods
                .Where(local => local.Matched?.Source == UpdateSource.Modrinth)
                .Select(local => local.Matched!.ProjectId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var projects = projectIds.Count == 0
                ? new Dictionary<string, ModrinthProject>()
                : (IReadOnlyDictionary<string, ModrinthProject>)await _modrinth.GetProjectsAsync(projectIds);

            foreach (var item in items)
            {
                if (!byPath.TryGetValue(item.Entry.Path, out var local)) continue;

                // 只补还没识别过的徽标：完整检查的结果比这里更权威，不覆盖
                if (!item.IsIdentified)
                    item.SetIdentification(local.Matched?.Source, local.Matched?.Channel);

                var iconUrl = "";

                if (local.Matched is { Source: UpdateSource.Modrinth } matched
                    && projects.TryGetValue(matched.ProjectId, out var project))
                {
                    iconUrl = project.IconUrl;

                    var icon = await _icons.GetAsync(iconUrl);
                    if (icon is not null) item.Icon = icon;
                }

                // 落盘：来源与版本通道也要记住，否则重启后徽标又得重新联网识别
                _contentMetadata.Set(item.Entry.FileName, new ContentMetadata(
                    Identified: true,
                    local.Matched?.Source,
                    local.Matched?.Channel,
                    iconUrl));
            }

            _contentMetadata.Save();
        }
        catch (Exception ex)
        {
            LauncherLog.Write(_launcher.Root, $"自动识别内容失败 → {ex.GetBaseException().Message}");
        }
    }

    /// <summary>逐个补齐搜索结果图标；取不到的留空，不阻塞列表。</summary>
    private async Task LoadSearchIconsAsync(IReadOnlyList<ModSearchResultItem> items)
    {
        foreach (var item in items)
        {
            var icon = await _icons.GetAsync(item.IconUrl);
            if (icon is not null) item.Icon = icon;
        }
    }

    /// <summary>
    /// 完整检查之后，把认领结果（来源 / R·B·A / 图标）写进持久缓存。
    /// 这样下次启动不用再联网，徽标与通道立即可见。
    /// </summary>
    private async Task SaveMetadataAsync(
        IReadOnlyList<ContentEntryItem> items,
        ModUpdateReport report)
    {
        try
        {
            var projectIds = report.LocalMods
                .Where(local => local.Matched?.Source == UpdateSource.Modrinth)
                .Select(local => local.Matched!.ProjectId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var projects = projectIds.Count == 0
                ? new Dictionary<string, ModrinthProject>()
                : (IReadOnlyDictionary<string, ModrinthProject>)await _modrinth.GetProjectsAsync(projectIds);

            var byPath = report.LocalMods.ToDictionary(local => local.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (!byPath.TryGetValue(item.Entry.Path, out var local)) continue;

                var iconUrl = "";

                if (local.Matched is { Source: UpdateSource.Modrinth } matched
                    && projects.TryGetValue(matched.ProjectId, out var project))
                {
                    iconUrl = project.IconUrl;

                    var icon = await _icons.GetAsync(iconUrl);
                    if (icon is not null) item.Icon = icon;
                }

                _contentMetadata.Set(item.Entry.FileName, new ContentMetadata(
                    Identified: true,
                    local.Matched?.Source,
                    local.Matched?.Channel,
                    iconUrl));
            }

            _contentMetadata.Save();

            LauncherLog.Write(_launcher.Root,
                $"内容识别已缓存：{items.Count} 个文件，Modrinth 项目 {projectIds.Count} 个");
        }
        catch (Exception ex)
        {
            LauncherLog.Write(_launcher.Root, $"缓存内容识别失败 → {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 详情加载的序号。
    ///
    /// 详情与翻译都是异步的：用户可能在看 A 的时候已经点了 B，
    /// 而 A 的请求晚于 B 返回 —— 不做判断就会把 A 的内容写到 B 的界面上。
    /// 每次发起加载都递增，只有"最后一次"才允许写回。
    /// </summary>
    private int _detailToken;

    private async Task LoadSearchDetailAsync(ModSearchResultItem item)
    {
        var token = ++_detailToken;

        IsDetailVisible = true;

        DetailTitle = item.Title;
        DetailSubtitle = $"{item.SourceLabel} · {item.MetaText}";
        DetailUrl = item.PageUrl;
        DetailTranslation = "";

        // 先用列表里已有的图标，等详情回来再按项目图标刷新
        DetailIcon = item.Icon;

        // CurseForge 的详情页在网页上；我们只拿到摘要，就不假装有正文
        if (item.Source == UpdateSource.CurseForge)
        {
            DetailBody = "点「在浏览器中打开」查看完整详情（CurseForge 的正文不通过 API 提供）。";
            return;
        }

        if (_detailBodyCache.TryGetValue(item.ProjectRef, out var cached))
        {
            DetailBody = cached;

            // 缓存只省了正文请求，翻译仍要补上（译文本身也有缓存，通常瞬间完成）
            await TranslateDetailAsync(token, cached);
            return;
        }

        try
        {
            DetailBody = "正在加载详情…";

            var detail = await _modrinth.GetProjectAsync(item.ProjectRef);
            if (token != _detailToken) return;

            if (detail is null)
            {
                DetailBody = "无法获取该项目详情";
                return;
            }

            var body = detail.BodySummary;
            if (string.IsNullOrWhiteSpace(body)) body = detail.Description;

            DetailTitle = detail.DisplayName;
            DetailSubtitle = $"Modrinth · {detail.Downloads:N0} 次下载"
                             + (detail.UpdatedAt == default ? "" : $" · 更新于 {detail.UpdatedAt.ToLocalTime():yyyy-MM-dd}")
                             + (detail.Categories.Count > 0 ? $" · {string.Join(", ", detail.Categories)}" : "");
            DetailUrl = detail.PageUrl;
            DetailBody = string.IsNullOrWhiteSpace(body) ? "(该项目没有提供描述)" : body;

            var icon = await _icons.GetAsync(detail.IconUrl);

            // 用户可能已经切到别的项目：晚到的结果不能覆盖当前界面
            if (token != _detailToken) return;

            if (icon is not null) DetailIcon = icon;

            _detailBodyCache[item.ProjectRef] = DetailBody;

            await TranslateDetailAsync(token, DetailBody);
        }
        catch (Exception ex)
        {
            if (token != _detailToken) return;
            DetailBody = $"获取详情失败：{ex.GetBaseException().Message}";
        }
    }

    private void OpenDetailInBrowser()
    {
        if (string.IsNullOrWhiteSpace(_detailUrl)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_detailUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status = $"无法打开浏览器：{ex.GetBaseException().Message}";
        }
    }

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

        SelectedSearchResult = null;
        IsDetailVisible = false;
        DetailTitle = "";
        DetailSubtitle = "";
        DetailBody = "";
        DetailUrl = "";

        SearchSummaryText = "";

        _pendingPlan = null;
        _pendingItem = null;
        PlanLines.Clear();
        InstallVersions.Clear();
        OnPropertyChanged(nameof(HasInstallVersions));
        IsPlanVisible = false;
        (ConfirmInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // 空查询也允许 —— 那是"浏览热门"，不是无效操作
    private bool CanSearch => !IsSearching && SelectedInstance is not null;

    private bool CanConfirmInstall => !IsInstalling && _pendingPlan is not null;

    /// <summary>
    /// 搜索。**查询为空时改为浏览热门**（按下载量排序）——
    /// 用户往往不知道该搜什么，先看当前版本/加载器下最常用的项目更有用。
    /// </summary>
    private async Task SearchModsAsync()
    {
        if (SelectedInstance is not { } instance) return;

        var query = _searchQuery.Trim();
        var browsing = query.Length == 0;

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

            // 搜索要用游戏版本号（1.20.1）；实例版本可能是 fabric-loader-0.20.1-1.20.1
            var gameVersion = await ResolveGameVersionAsync(instance);

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

            Status = browsing
                ? $"正在 {source} 获取热门{kind.DisplayName()}…"
                : $"正在 {source} 搜索「{query}」…";

            SearchResults.Clear();
            SelectedSearchResult = null;
            IsDetailVisible = false;

            if (source == UpdateSource.CurseForge)
            {
                var hits = await _curseForge!.SearchAsync(
                    query, gameVersion, loaders, 8, kind.CurseForgeClassId()!.Value);

                var pageSegment = kind == ContentKind.ResourcePack ? "texture-packs" : "mc-mods";

                foreach (var hit in hits)
                {
                    SearchResults.Add(new ModSearchResultItem(
                        UpdateSource.CurseForge,
                        hit.ModId.ToString(),
                        hit.DisplayName,
                        hit.Slug,
                        hit.DownloadCount,
                        BeginInstallAsync)
                    {
                        PageUrl = $"https://www.curseforge.com/minecraft/{pageSegment}/{hit.Slug}",
                        IconUrl = hit.LogoUrl,
                    });
                }
            }
            else
            {
                var hits = await _modrinth.SearchAsync(
                    query, gameVersion, loaders, 8, kind.ModrinthProjectType(),
                    browsing ? "downloads" : "relevance");

                foreach (var hit in hits)
                {
                    SearchResults.Add(new ModSearchResultItem(
                        UpdateSource.Modrinth,
                        hit.Slug,
                        hit.DisplayName,
                        hit.Slug,
                        hit.Downloads,
                        BeginInstallAsync)
                    {
                        PageUrl = $"https://modrinth.com/{kind.ModrinthProjectType()}/{hit.Slug}",
                        IconUrl = hit.IconUrl,
                    });
                }
            }

            OnPropertyChanged(nameof(HasSearchResults));

            // 图标是次要信息：先让列表出来，再逐个把图标填上
            _ = LoadSearchIconsAsync(SearchResults.ToList());

            // 搜出结果就展开 —— 收起状态下搜完看不到东西会让人以为没搜到
            IsSearchPanelExpanded = true;

            SearchSummaryText = SearchResults.Count == 0
                ? browsing
                    ? $"{source} 上没有找到适用于 {gameVersion} 的{kind.DisplayName()}"
                    : $"{source} 上没有找到适用于 {gameVersion} 的{kind.DisplayName()}"
                : browsing
                    ? $"热门{kind.DisplayName()}（{source} · {gameVersion}"
                      + (loaders.Count > 0 ? " / " + string.Join(",", loaders) : "") + " · 按下载量）"
                    : $"找到 {SearchResults.Count} 个{kind.DisplayName()}"
                      + $"（{source} · {gameVersion}"
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

            // 解析项目版本、装依赖，用的都是**游戏版本号**，不是加载器 profile 名
            var gameVersion = await ResolveGameVersionAsync(instance);
            _pendingGameVersion = gameVersion;

            Status = $"正在查询 {item.Title} 的可用版本…";

            var choices = await LoadVersionChoicesAsync(item, gameVersion, loaders);

            if (choices.Count == 0)
            {
                Status = $"{item.Title} 没有适用于 {gameVersion} 的版本";
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
                    _pendingGameVersion,
                    _pendingLoaders,
                    includeDependencies: _pendingKind.HasDependencies(),
                    rootTitle: item.Title,
                    fileId: int.TryParse(choice.Ref, out var fileId) ? fileId : null);
            }
            else
            {
                plan = await _installer.ResolveAsync(
                    item.ProjectRef,
                    _pendingGameVersion,
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

    /// <summary>
    /// 实例的**游戏版本号**（如 1.20.1），沿 inheritsFrom 解析。
    /// 直接用实例的 VersionId 去问 Modrinth 是不行的 —— 那可能是 fabric-loader-0.20.1-1.20.1。
    /// </summary>
    private Task<string> ResolveGameVersionAsync(InstanceListItem instance)
        => GameLauncher.ResolveGameVersionAsync(_launcher, instance.VersionId);

    // ---- 导航与下载页 ----

    private NavPage _navPage = NavPage.Instances;
    private int _versionFilterIndex;
    private bool _isDownloading;
    // 默认不装加载器：直接玩原版是最常见的路径，加载器应当由用户主动勾选
    private bool _installFabric;
    private bool _versionsLoaded;
    private string _downloadStatus = "尚未加载版本清单";    private string _downloadProgressText = "";
    private string _downloadInstanceName = "";
    private DownloadVersionItem? _selectedDownloadVersion;

    public bool IsInstancesPage => _navPage == NavPage.Instances;

    public bool IsDownloadsPage => _navPage == NavPage.Downloads;

    public string InstancesNavBackground => IsInstancesPage ? "#2B3A55" : "Transparent";

    public string DownloadsNavBackground => IsDownloadsPage ? "#2B3A55" : "Transparent";

    public string InstancesNavForeground => IsInstancesPage ? "#DCE4F5" : "#949CA9";

    public string DownloadsNavForeground => IsDownloadsPage ? "#DCE4F5" : "#949CA9";

    public ObservableCollection<DownloadVersionItem> DownloadVersions { get; } = new();

    public DownloadVersionItem? SelectedDownloadVersion
    {
        get => _selectedDownloadVersion;
        set
        {
            if (!SetProperty(ref _selectedDownloadVersion, value)) return;

            OnPropertyChanged(nameof(DownloadVersionSubtitle));
            OnPropertyChanged(nameof(DownloadConflictsWithExisting));

            // 名称默认跟随版本；用户手改过就不再覆盖
            SyncInstanceNameFromSelection();

            // 该版本需要哪个 Java、本机有没有，选中就去查
            _ = RefreshJavaStateAsync();

            // 换了游戏版本，旧加载器列表不再适用
            FabricLoaderVersions.Clear();
            SelectedFabricLoader = null;
            OnPropertyChanged(nameof(HasFabricLoaderVersions));
            if (_installFabric) _ = LoadFabricLoaderVersionsAsync();

            (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>详情页标题下的一行说明。</summary>
    public string DownloadVersionSubtitle => _selectedDownloadVersion is { } version
        ? $"{version.TypeLabel} · 发布于 {version.ReleasedText}"
        : "请选择一个版本";

    // ---- Java 运行时 ----

    private bool _installJava = true;
    private bool _javaInstalled;
    private string _javaStatusText = "";
    private string _requiredJavaComponent = "java-runtime-gamma";
    private int _requiredJavaMajor = 21;

    /// <summary>是否顺带下载该版本所需的 Java 运行时。</summary>
    public bool InstallJava
    {
        get => _installJava;
        set => SetProperty(ref _installJava, value);
    }

    public bool IsJavaInstalled => _javaInstalled;

    /// <summary>Java 状况的一行说明（缺不缺、装在哪）。</summary>
    public string JavaStatusText
    {
        get => _javaStatusText;
        private set => SetProperty(ref _javaStatusText, value);
    }

    /// <summary>
    /// 读版本 JSON 得知所需的 Java 组件与主版本，再看本机是否已有。
    ///
    /// 不能一律用 java-runtime-gamma：新版需要更新的运行时（1.20.5+ 是 Java 21），
    /// 而 1.16 及更早需要 Java 8。具体用哪个只能问版本元数据。
    /// </summary>
    private int _javaToken;

    private async Task RefreshJavaStateAsync()
    {
        if (_selectedDownloadVersion is not { } selected) return;

        // 快速切换版本时，先发出的查询可能后返回，把新版本的结果冲掉。
        // 用序号只让"最后一次"写回。
        var token = ++_javaToken;

        try
        {
            var installer = await CreateMinecraftInstallerAsync();
            var version = await installer.LoadVersionAsync(_launcher, selected.Id);

            if (token != _javaToken) return;

            _requiredJavaComponent = version.JavaVersion?.Component is { Length: > 0 } component
                ? component
                : "java-runtime-gamma";

            _requiredJavaMajor = version.JavaVersion?.MajorVersion ?? 21;

            var exe = _launcher.JavaExe(_requiredJavaComponent);
            _javaInstalled = File.Exists(exe);

            OnPropertyChanged(nameof(IsJavaInstalled));

            JavaStatusText = _javaInstalled
                ? $"已安装 Java {_requiredJavaMajor}（{_requiredJavaComponent}）"
                : $"缺少 Java {_requiredJavaMajor}，安装时会自动下载 {_requiredJavaComponent}";

            // 已经装了就不必再勾着
            InstallJava = !_javaInstalled;
        }
        catch (Exception ex)
        {
            JavaStatusText = $"无法确认 Java 需求：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"查询 Java 需求失败 → {ex.GetBaseException().Message}");
        }
    }

    /// <summary>按需安装该版本所需的 Java 运行时。</summary>
    private async Task InstallJavaIfNeededAsync(IProgress<string>? progress)
    {
        if (!InstallJava) return;

        if (File.Exists(_launcher.JavaExe(_requiredJavaComponent)))
        {
            _javaInstalled = true;
            OnPropertyChanged(nameof(IsJavaInstalled));
            return;
        }

        progress?.Report($"正在下载 Java 运行时 {_requiredJavaComponent}…");

        var settings = await new JsonSettingsStore(_launcher).LoadAsync();
        var runtime = new MojangRuntimeInstaller(_downloader, _downloader.GetStringAsync);

        var result = await runtime.InstallAsync(
            "windows-x64",
            _requiredJavaComponent,
            _launcher,
            settings.DownloadConcurrency);

        _javaInstalled = true;
        OnPropertyChanged(nameof(IsJavaInstalled));

        JavaStatusText = $"已安装 Java {_requiredJavaMajor}（{result.TotalFiles} 个文件）";
        LauncherLog.Write(_launcher.Root,
            $"Java 运行时 {_requiredJavaComponent} 安装完成：{result.TotalFiles} 个文件，失败 {result.Failures.Count} 个");
    }

    // ---- Fabric 加载器版本 ----

    private FabricLoaderVersion? _selectedFabricLoader;
    private bool _instanceNameEdited;
    private bool _suppressNameEdit;

    public ObservableCollection<FabricLoaderVersion> FabricLoaderVersions { get; } = new();

    public bool HasFabricLoaderVersions => FabricLoaderVersions.Count > 0;

    public FabricLoaderVersion? SelectedFabricLoader
    {
        get => _selectedFabricLoader;
        set
        {
            if (!SetProperty(ref _selectedFabricLoader, value)) return;

            // 名称里带加载器版本，两个不同加载器的实例才不会同名
            SyncInstanceNameFromSelection();
        }
    }

    /// <summary>默认实例名：版本号；勾了 Fabric 时再带上加载器版本。</summary>
    private string DefaultInstanceName()
    {
        var version = _selectedDownloadVersion?.Id ?? "";
        if (string.IsNullOrWhiteSpace(version)) return "";

        return _installFabric && _selectedFabricLoader is { } loader
            ? $"{version}-fabric-{loader.Version}"
            : version;
    }

    /// <summary>用户没手改过名称时，让它跟着版本/加载器走。</summary>
    private void SyncInstanceNameFromSelection()
    {
        if (_instanceNameEdited) return;

        var name = DefaultInstanceName();
        if (string.IsNullOrWhiteSpace(name) || name == _downloadInstanceName) return;

        _suppressNameEdit = true;
        DownloadInstanceName = name;
        _suppressNameEdit = false;
    }

    private async Task LoadFabricLoaderVersionsAsync()
    {
        if (_selectedDownloadVersion is not { } version) return;

        try
        {
            DownloadStatus = "正在获取 Fabric 加载器版本…";

            var fabric = new FabricLoaderInstaller(_http);
            var versions = await fabric.GetLoaderVersionsAsync(version.Id);

            FabricLoaderVersions.Clear();
            foreach (var loader in versions) FabricLoaderVersions.Add(loader);

            OnPropertyChanged(nameof(HasFabricLoaderVersions));
            SelectedFabricLoader = FabricLoaderVersions.FirstOrDefault(loader => loader.Stable)
                                   ?? FabricLoaderVersions.FirstOrDefault();

            if (versions.Count == 0)
            {
                // 不支持就别让用户勾着：取消勾选，并说明原因。
                // 空列表有三种来源：早于 Fabric 的版本（400）、
                // 以及没有 intermediary 映射、只返回 0.0.0 占位符的版本。
                InstallFabric = false;
                DownloadStatus = $"Fabric 不支持 {version.Id}（该版本没有对应的 intermediary 映射）";
                LauncherLog.Write(_launcher.Root, $"Fabric 不支持 {version.Id}，已取消勾选");
                return;
            }

            DownloadStatus = $"共 {versions.Count} 个 Fabric 加载器版本";
        }
        catch (Exception ex)
        {
            DownloadStatus = $"获取 Fabric 加载器版本失败：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>实际会用的实例名：留空就用默认名（版本号 / 带加载器版本）。</summary>
    public string EffectiveInstanceName =>
        string.IsNullOrWhiteSpace(_downloadInstanceName)
            ? DefaultInstanceName()
            : _downloadInstanceName.Trim();

    /// <summary>是否与已有实例重名（重名会让人分不清哪个是新的）。</summary>
    public bool DownloadConflictsWithExisting =>
        EffectiveInstanceName.Length > 0
        && Instances.Any(item =>
            string.Equals(item.Name, EffectiveInstanceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>新实例名。留空表示使用默认名；手改后不再跟随版本/加载器。</summary>
    public string DownloadInstanceName
    {
        get => _downloadInstanceName;
        set
        {
            if (!SetProperty(ref _downloadInstanceName, value)) return;

            // 只有"非空输入"才算用户改名。
            // TextBox 挂载时会先把它自己的空值写回来，若把那也算改名，
            // 后续的"跟随版本"就全部失效 —— 名称会一直是空、按钮也永远灰着。
            if (!_suppressNameEdit && !string.IsNullOrWhiteSpace(value)) _instanceNameEdited = true;

            OnPropertyChanged(nameof(EffectiveInstanceName));
            OnPropertyChanged(nameof(DownloadConflictsWithExisting));
            (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 版本类型过滤：0 仅正式版 / 1 正式版+快照 / 2 全部（含早期 alpha、beta）。
    ///
    /// 愚人节版本在 Mojang 的清单里就是 snapshot，所以"含快照"就能看到它们；
    /// 而 2010 年前后的早期版本被标成 old_alpha / old_beta，需要"全部"才出现。
    /// </summary>
    public int VersionFilterIndex
    {
        get => _versionFilterIndex;
        set
        {
            if (!SetProperty(ref _versionFilterIndex, value)) return;

            _versionsLoaded = false;
            OnPropertyChanged(nameof(VersionFilterText));
            _ = LoadDownloadVersionsAsync();
        }
    }

    /// <summary>是否同时安装 Fabric 加载器（装完才能用模组）。</summary>
    public bool InstallFabric
    {
        get => _installFabric;
        set
        {
            if (!SetProperty(ref _installFabric, value)) return;

            OnPropertyChanged(nameof(HasFabricLoaderVersions));
            SyncInstanceNameFromSelection();

            if (value && FabricLoaderVersions.Count == 0) _ = LoadFabricLoaderVersionsAsync();
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public string DownloadProgressText
    {
        get => _downloadProgressText;
        private set => SetProperty(ref _downloadProgressText, value);
    }

    public ICommand ShowInstancesCommand { get; }
    public ICommand ShowDownloadsCommand { get; }
    public ICommand DownloadCommand { get; }

    // 名字为空或与已有实例重名时不允许开始下载 —— 先把名字改好再装

    private bool CanDownload => !IsDownloading
                                && _selectedDownloadVersion is not null
                                && EffectiveInstanceName.Length > 0
                                && !DownloadConflictsWithExisting;

    private void Navigate(NavPage page)
    {
        if (_navPage == page) return;

        _navPage = page;

        OnPropertyChanged(nameof(IsInstancesPage));
        OnPropertyChanged(nameof(IsDownloadsPage));
        OnPropertyChanged(nameof(InstancesNavBackground));
        OnPropertyChanged(nameof(DownloadsNavBackground));
        OnPropertyChanged(nameof(InstancesNavForeground));
        OnPropertyChanged(nameof(DownloadsNavForeground));
        OnPropertyChanged(nameof(ShowVersionList));
        OnPropertyChanged(nameof(IsVersionsMode));
        OnPropertyChanged(nameof(IsModpacksMode));

        if (page == NavPage.Downloads) _ = LoadDownloadVersionsAsync();
    }

    /// <summary>拉取版本清单。只在下拉页可见时才做，避免启动时多一次网络请求。</summary>
    private async Task LoadDownloadVersionsAsync()
    {
        if (_versionsLoaded || IsDownloading) return;

        try
        {
            DownloadStatus = "正在获取版本清单…";
            var installer = await CreateMinecraftInstallerAsync();

            var versions = await installer.GetVersionsAsync();

            DownloadVersions.Clear();

            foreach (var entry in versions)
            {
                if (!AcceptVersion(entry)) continue;

                DownloadVersions.Add(new DownloadVersionItem(entry));
            }

            _versionsLoaded = DownloadVersions.Count > 0;

            if (DownloadVersions.Count > 0)
            {
                SelectedDownloadVersion ??= DownloadVersions[0];
                (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                DownloadStatus = $"共 {DownloadVersions.Count} 个版本（{VersionFilterText}）";
            }
            else
            {
                DownloadStatus = "清单里没有可用版本";
            }
        }
        catch (Exception ex)
        {
            DownloadStatus = $"获取版本清单失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"获取版本清单失败 → {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 下载版本 →（可选）装 Fabric → 建实例并选中。
    ///
    /// 三步是一体的：只下本体不建实例，用户还得再手动建一次；
    /// 而装了 Fabric 却不建对应版本的实例，模组依然不会被加载。
    /// </summary>
    private async Task DownloadAsync()
    {
        if (_selectedDownloadVersion is not { } choice) return;

        IsDownloading = true;
        DownloadProgressText = "";

        try
        {
            var installer = await CreateMinecraftInstallerAsync();

            DownloadStatus = $"正在获取 {choice.Id} 的版本信息…";
            var version = await installer.LoadVersionAsync(_launcher, choice.Id);

            var progress = new Progress<MinecraftInstallProgress>(p =>
            {
                DownloadProgressText = p.Stage switch
                {
                    "assets" => $"资源对象 {p.Completed}/{p.Total}   已下载 {p.BytesDownloaded / 1024d / 1024d:N1} MB",
                    "libraries" => $"库 {p.Completed}/{p.Total}",
                    _ => p.Stage,
                };
            });

            DownloadStatus = $"正在下载 {choice.Id}…";
            var result = await installer.InstallAsync(_launcher, choice.Id, version, progress);

            DownloadStatus = $"本体完成：库 {result.LibrariesDownloaded} 个，资源对象 {result.AssetObjects} 个"
                             + $"（新下载 {result.AssetsDownloaded} 个 / {result.AssetBytes / 1024d / 1024d:N1} MB）"
                             + (result.Failures.Count > 0 ? $"，失败 {result.Failures.Count} 个" : "");

            // Java 运行时：版本元数据要求哪个组件就装哪个（缺了才下）
            await InstallJavaIfNeededAsync(new Progress<string>(message => DownloadProgressText = message));


            // 可选：装 Fabric，并把实例指向 Fabric 的 profile 版本
            var instanceVersionId = choice.Id;

            if (_installFabric)
            {
                DownloadStatus = $"正在安装 Fabric 加载器（游戏 {choice.Id}）…";
                DownloadProgressText = "";

                var fabric = new FabricLoaderInstaller(_http);
                // 用界面上选定的加载器版本；未选则让安装器取最新 stable
                var fabricResult = await fabric.InstallAsync(
                    _launcher, choice.Id, _selectedFabricLoader?.Version, _downloader);

                instanceVersionId = fabricResult.ProfileId;
                DownloadStatus = $"Fabric {fabricResult.LoaderVersion} 已安装（库 {fabricResult.LibrariesDownloaded} 个）";
            }

            // 建实例：版本指向 fabric profile（若有），这样页签能力检测能识别到加载器
            var store = new JsonInstanceStore(_launcher);
            var catalog = new InstanceCatalog(await store.LoadAsync());

            var name = EffectiveInstanceName;
            // 记下 Java 组件，启动时就不必再猜
            var instance = catalog.Add(name, instanceVersionId, settings: _javaInstalled
                ? new InstanceSettings { JavaComponent = _requiredJavaComponent }
                : null);

            await store.SaveAsync(catalog.Instances);

            DownloadStatus = $"完成：已创建实例「{instance.Name}」（版本 {instanceVersionId}）";
            DownloadProgressText = "";

            // 回到实例页并选中新建的那个；页签显式落回概览，
            // 否则新实例若不支持当前页签（比如原版打开着"模组"），会没有选中项
            await LoadAsync();
            Navigate(NavPage.Instances);
            SelectedTab = ContentTabs[0];

            SelectedInstance = Instances.FirstOrDefault(item =>
                string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            DownloadStatus = $"下载失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"下载 {choice.Id} 失败 → {ex.GetBaseException().Message}");
        }
        finally
        {
            IsDownloading = false;
            (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>当前过滤条件是否接受该版本类型。</summary>
    /// <summary>
    /// 当前过滤条件是否接受该版本。
    ///
    /// "正式版 + 快照"**排除**愚人节版本，否则专门的"仅愚人节"就没有意义。
    /// 愚人节版本只能靠版本号与日期识别 —— 清单里的 type 与普通快照完全相同。
    /// </summary>
    private bool AcceptVersion(VersionManifestEntry entry)
    {
        var isRelease = string.Equals(entry.Type, "release", StringComparison.OrdinalIgnoreCase);
        var isSnapshot = string.Equals(entry.Type, "snapshot", StringComparison.OrdinalIgnoreCase);
        var isAprilFools = AprilFoolsVersions.IsAprilFools(entry.Id, entry.ReleaseTime, entry.Type);

        return _versionFilterIndex switch
        {
            0 => isRelease,
            1 => (isRelease || isSnapshot) && !isAprilFools,
            2 => true,
            3 => isAprilFools,
            _ => true,
        };
    }

    /// <summary>过滤条件的中文描述（用于状态行）。</summary>
    public string VersionFilterText => _versionFilterIndex switch
    {
        0 => "仅正式版",
        1 => "正式版 + 快照（不含愚人节）",
        2 => "全部，含早期 alpha/beta",
        _ => "仅愚人节版本",
    };

    /// <summary>按全局设置构造安装器（镜像源、并发数都取自设置）。</summary>
    private async Task<MinecraftInstaller> CreateMinecraftInstallerAsync()
    {
        var settings = await new JsonSettingsStore(_launcher).LoadAsync();
        var router = new MirrorRouter(settings.ToMirrorSettings());
        var source = VersionManifestSource.Create(router, _downloader);

        return new MinecraftInstaller(_downloader, router, source, settings.DownloadConcurrency);
    }

    // ---- 下载页：区域切换与本地整合包 ----

    private DownloadMode _downloadMode = DownloadMode.Versions;
    private ModpackPlan? _modpackPlan;
    private bool _isInstallingModpack;
    private string _modpackStatus = "选择一个 .mrpack 文件，或直接把它拖到这里";
    private string _modpackProgress = "";

    // 必须同时判断"在下载页"与"选了哪个区块"：只判断区块的话，
    // 这两个面板在实例页也会显示，直接压在实例详情上面。

    public bool IsVersionsMode => IsDownloadsPage && _downloadMode == DownloadMode.Versions;

    public bool IsModpacksMode => IsDownloadsPage && _downloadMode == DownloadMode.Modpacks;

    /// <summary>下载页左侧版本列表只在"游戏版本"区域显示。</summary>
    public bool ShowVersionList => IsVersionsMode;

    public string VersionsModeBackground => IsVersionsMode ? "#2B3A55" : "Transparent";

    public string ModpacksModeBackground => IsModpacksMode ? "#2B3A55" : "Transparent";

    public string VersionsModeForeground => IsVersionsMode ? "#DCE4F5" : "#949CA9";

    public string ModpacksModeForeground => IsModpacksMode ? "#DCE4F5" : "#949CA9";

    public ICommand ShowVersionsModeCommand { get; }
    public ICommand ShowModpacksModeCommand { get; }
    public ICommand InstallModpackCommand { get; }

    /// <summary>整合包状态行（选择文件后显示解析结果）。</summary>
    public string ModpackStatus
    {
        get => _modpackStatus;
        private set => SetProperty(ref _modpackStatus, value);
    }

    public string ModpackProgress
    {
        get => _modpackProgress;
        private set => SetProperty(ref _modpackProgress, value);
    }

    public bool IsInstallingModpack
    {
        get => _isInstallingModpack;
        private set
        {
            if (!SetProperty(ref _isInstallingModpack, value)) return;
            (InstallModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasModpackPlan => _modpackPlan is not null;

    public string ModpackTitle => _modpackPlan?.Name ?? "尚未选择整合包";

    /// <summary>解析出来的关键信息：游戏版本、加载器、文件数、大小。</summary>
    public string ModpackSummary => _modpackPlan is not { } plan
        ? ""
        : $"游戏版本 {plan.Index.GameVersion} · 加载器 {plan.Index.LoaderText}"
          + $" · {plan.Index.Files.Count} 个文件（{plan.Index.ModCount} 个模组）"
          + $" · 约 {plan.Index.TotalBytes / 1024d / 1024d:N0} MB"
          + (plan.VersionLabel.Length > 0 ? $" · 版本 {plan.VersionLabel}" : "");

    private void SetDownloadMode(DownloadMode mode)
    {
        if (_downloadMode == mode) return;

        _downloadMode = mode;

        OnPropertyChanged(nameof(IsVersionsMode));
        OnPropertyChanged(nameof(IsModpacksMode));
        OnPropertyChanged(nameof(ShowVersionList));
        OnPropertyChanged(nameof(VersionsModeBackground));
        OnPropertyChanged(nameof(ModpacksModeBackground));
        OnPropertyChanged(nameof(VersionsModeForeground));
        OnPropertyChanged(nameof(ModpacksModeForeground));
    }

    /// <summary>界面侧的错误（例如拖入的不是 .mrpack）也要能显示出来。</summary>
    public void ReportModpackFailure(string message) => ModpackStatus = message;

    /// <summary>
    /// 载入本地整合包（文件对话框或拖入都会走这里）。
    /// 只解析、不安装 —— 先给用户看清楚"这是什么包、要什么版本"。
    /// </summary>
    public async Task LoadModpackFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsInstallingModpack) return;

        try
        {
            ModpackStatus = $"正在解析 {Path.GetFileName(path)}…";
            ModpackProgress = "";

            var installer = CreateModpackInstaller();
            var plan = await installer.PrepareFromFileAsync(
                path,
                Path.Combine(_launcher.Root, "cache", "modpacks"));

            _modpackPlan = plan;

            OnPropertyChanged(nameof(HasModpackPlan));
            OnPropertyChanged(nameof(ModpackTitle));
            OnPropertyChanged(nameof(ModpackSummary));
            (InstallModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            ModpackStatus = plan.Problems.Count == 0
                ? $"已就绪：{plan.Name}"
                : $"已就绪：{plan.Name}（{plan.Problems.Count} 个覆盖层条目无法解压）";
        }
        catch (Exception ex)
        {
            _modpackPlan = null;

            OnPropertyChanged(nameof(HasModpackPlan));
            OnPropertyChanged(nameof(ModpackTitle));
            OnPropertyChanged(nameof(ModpackSummary));
            (InstallModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            ModpackStatus = $"无法读取该文件：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"解析整合包失败 {path} → {ex.GetBaseException().Message}");
        }
    }

    private ModpackInstaller CreateModpackInstaller()
        => new(_downloader, _modrinth);

    /// <summary>
    /// 安装本地整合包：本体 → 加载器 → 建实例 → 文件与覆盖层。
    ///
    /// 顺序很重要：实例必须先存在，文件才有地方放；而本体与加载器是共享的，
    /// 装一次即可被多个实例复用。
    /// </summary>
    private async Task InstallModpackAsync()
    {
        if (_modpackPlan is not { } plan || IsInstallingModpack) return;

        var gameVersion = plan.Index.GameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            ModpackStatus = "该整合包没有声明游戏版本，无法安装";
            return;
        }

        IsInstallingModpack = true;

        try
        {
            var progress = new Progress<string>(message => ModpackProgress = message);

            ModpackStatus = $"正在下载游戏本体 {gameVersion}…";
            var installer = await CreateMinecraftInstallerAsync();
            var version = await installer.LoadVersionAsync(_launcher, gameVersion);
            await installer.InstallAsync(_launcher, gameVersion, version);

            var instanceVersion = gameVersion;

            if (plan.Index.IsFabric)
            {
                ModpackStatus = $"正在安装 Fabric {plan.Index.LoaderVersion}…";

                var fabric = new FabricLoaderInstaller(_http);
                var fabricResult = await fabric.InstallAsync(
                    _launcher, gameVersion, plan.Index.LoaderVersion, _downloader);

                instanceVersion = fabricResult.ProfileId;
            }
            else if (plan.Index.LoaderName is { } otherLoader)
            {
                ModpackStatus = $"该整合包需要 {otherLoader}，目前只能自动安装 Fabric；"
                                + "将按原版创建，加载器需另行安装。";
            }

            // 建实例（名字重名时自动加序号）
            var store = new JsonInstanceStore(_launcher);
            var catalog = new InstanceCatalog(await store.LoadAsync());

            var name = UniqueName(catalog, plan.Name);
            var instance = catalog.Add(name, instanceVersion);
            await store.SaveAsync(catalog.Instances);

            ModpackStatus = $"正在写入整合包文件（{plan.Index.Files.Count} 个）…";
            var layout = new InstanceLayout(_launcher, instance);

            var result = await CreateModpackInstaller()
                .InstallIntoAsync(plan, layout.GameDirectory, progress);

            ModpackProgress = "";

            ModpackStatus = $"完成：已创建「{instance.Name}」"
                            + $"（文件 {result.FilesDownloaded} 个，跳过 {result.FilesSkipped} 个，"
                            + $"覆盖层 {result.OverrideFiles} 个"
                            + (result.Failures.Count > 0 ? $"，失败 {result.Failures.Count} 个" : "")
                            + "）";

            LauncherLog.Write(_launcher.Root,
                $"安装整合包 {plan.Name} → 实例 {instance.Name}：文件 {result.FilesDownloaded}，"
                + $"覆盖层 {result.OverrideFiles}，失败 {result.Failures.Count}");

            // 回到实例页并选中它
            await LoadAsync();
            Navigate(NavPage.Instances);
            SelectedTab = ContentTabs[0];
            SelectedInstance = Instances.FirstOrDefault(item =>
                string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ModpackStatus = $"安装失败：{ex.GetBaseException().Message}";
            LauncherLog.Write(_launcher.Root, $"安装整合包失败 → {ex.GetBaseException().Message}");
        }
        finally
        {
            IsInstallingModpack = false;
        }
    }

    /// <summary>重名时加序号，避免两个实例同名到分不清。</summary>
    private static string UniqueName(InstanceCatalog catalog, string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "整合包" : name.Trim();
        var candidate = baseName;

        for (var index = 2;
             catalog.Instances.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase));
             index++)
        {
            candidate = $"{baseName} ({index})";
        }

        return candidate;
    }

    // ---- 实例的 Java 选择 ----

    private JavaChoice? _selectedJavaChoice;
    private string _javaHint = "";

    /// <summary>可选的 Java：自动、启动器自带的运行时、系统里发现的。</summary>
    public ObservableCollection<JavaChoice> JavaChoices { get; } = new();

    public JavaChoice? SelectedJavaChoice
    {
        get => _selectedJavaChoice;
        set
        {
            if (!SetProperty(ref _selectedJavaChoice, value)) return;

            _ = ApplyJavaChoiceAsync(value);
        }
    }

    /// <summary>当前选择的说明（路径或"由版本决定"）。</summary>
    public string JavaHint
    {
        get => _javaHint;
        private set
        {
            if (!SetProperty(ref _javaHint, value)) return;
            OnPropertyChanged(nameof(HasJavaHint));
        }
    }

    /// <summary>有提示文字时才占那一行，没有就完全不占空间。</summary>
    public bool HasJavaHint => !string.IsNullOrWhiteSpace(_javaHint);

    /// <summary>为某实例列出候选 Java，并选中它当前用的那个。</summary>
    /// <summary>
    /// 列出候选 Java，按用户期望的优先级分三档：
    ///   1. 启动器目录附近（runtime/* 、jre/ 、java/）—— 最可能是"就是为这个启动器准备的"
    ///   2. 系统默认安装位置（Program Files 各厂商、PATH、JAVA_HOME）
    ///   3. 用户曾经手动指定的非常规路径
    /// </summary>
    private int _javaChoicesToken;

    private async Task LoadJavaChoicesAsync(InstanceListItem? instance)
    {
        // 这个方法会被多处触发（选中实例、刷新内容、加载完成）。
        // 直接往 JavaChoices 里填会让并发的几次调用互相叠加，出现重复项；
        // 因此先建本地列表，最后用序号确认"我仍是最新的一次"再整体替换。
        var token = ++_javaChoicesToken;
        var built = new List<JavaChoice> { new() { Label = "自动（用该版本要求的 Java）" } };

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddUnique(string label, string exe, string? component)
            {
                if (!File.Exists(exe)) return;
                if (!seen.Add(JavaLocator.Normalize(exe))) return;

                built.Add(new JavaChoice { Label = label, Path = exe, Component = component });
            }

            // ---- 1) 启动器目录 ----
            foreach (var entry in EnumerateLocalJava())
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(entry.Exe)));
                AddUnique($"本地 · {entry.Component ?? parent ?? "java"}", entry.Exe, entry.Component);
            }

            // ---- 2) 系统默认位置 ----
            var system = await Task.Run(() => JavaLocator.DiscoverSystem());
            foreach (var java in system)
                AddUnique($"系统 · Java {java.MajorVersion} · {java.Path}", java.Path, null);

            // ---- 3) 用户指定的非常规路径 ----
            if (instance?.JavaPath is { Length: > 0 } custom)
            {
                var major = JavaLocator.ReadMajorVersion(custom);
                AddUnique($"指定 · {(major is null ? "Java" : $"Java {major}")} · {custom}", custom, null);
            }

            if (token != _javaChoicesToken) return;

            JavaChoices.Clear();
            foreach (var choice in built) JavaChoices.Add(choice);

            // 选中实例现有的设置
            var current = JavaChoices.FirstOrDefault(choice =>
                              instance?.JavaPath is { } path
                              && choice.Path is not null
                              && JavaLocator.Normalize(choice.Path) == JavaLocator.Normalize(path))
                          ?? JavaChoices.FirstOrDefault(choice =>
                              instance?.JavaComponent is { } component
                              && string.Equals(choice.Component, component, StringComparison.OrdinalIgnoreCase))
                          ?? JavaChoices[0];

            _selectedJavaChoice = current;
            OnPropertyChanged(nameof(SelectedJavaChoice));
            JavaHint = current.Path ?? "由版本元数据决定（缺了会在下载页自动补）";
        }
        catch (Exception ex)
        {
            JavaHint = $"枚举 Java 失败：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>启动器目录里能直接用的 Java：runtime/&lt;组件&gt;、jre、java。</summary>
    private IEnumerable<(string Exe, string? Component)> EnumerateLocalJava()
    {
        if (Directory.Exists(_launcher.Runtime))
        {
            foreach (var directory in Directory.EnumerateDirectories(_launcher.Runtime).OrderBy(path => path))
            {
                var exe = Path.Combine(directory, "bin", "java.exe");
                if (File.Exists(exe)) yield return (exe, Path.GetFileName(directory));
            }
        }

        foreach (var name in new[] { "jre", "java", "jdk" })
        {
            var exe = Path.Combine(_launcher.Root, name, "bin", "java.exe");
            if (File.Exists(exe)) yield return (exe, null);
        }
    }

    /// <summary>用户通过"浏览"指定的 java.exe。</summary>
    public void SetCustomJava(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || SelectedInstance is null) return;

        var normalized = JavaLocator.Normalize(path);
        var existing = JavaChoices.FirstOrDefault(choice =>
            choice.Path is not null && JavaLocator.Normalize(choice.Path) == normalized);

        if (existing is null)
        {
            var major = JavaLocator.ReadMajorVersion(path);
            existing = new JavaChoice
            {
                Label = $"指定 · {(major is null ? "Java" : $"Java {major}")} · {path}",
                Path = path,
            };

            JavaChoices.Add(existing);
        }

        SelectedJavaChoice = existing;
    }
    /// <summary>把选择写回实例设置。</summary>
    private async Task ApplyJavaChoiceAsync(JavaChoice? choice)
    {
        if (choice is null || SelectedInstance is not { } instance) return;

        try
        {
            var store = new JsonInstanceStore(_launcher);
            var catalog = new InstanceCatalog(await store.LoadAsync());

            var current = catalog.Find(instance.Id);
            if (current is null) return;

            // 只改 Java 两项，其余设置（内存、分辨率、附加参数）原样保留
            var settings = current.Settings with
            {
                JavaPath = choice.Path,
                JavaComponent = choice.Component,
            };

            catalog.UpdateSettings(instance.Id, settings);
            await store.SaveAsync(catalog.Instances);

            JavaHint = choice.Path ?? "由版本元数据决定（缺了会在下载页自动补）";

            LauncherLog.Write(_launcher.Root,
                $"实例 {instance.Name} 的 Java 设为：{choice.Label}");
        }
        catch (Exception ex)
        {
            JavaHint = $"保存失败：{ex.GetBaseException().Message}";
        }
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

        // 搜索的可用性取决于"有没有选中实例" —— 选中前按钮会一直是灰的，
        // 所以实例变化时也必须通知它（否则空查询时「热门」永远点不动）。
        (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
