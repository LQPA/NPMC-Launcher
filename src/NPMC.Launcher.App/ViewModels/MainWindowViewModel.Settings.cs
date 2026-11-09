using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Launch;
using NPMC.Launcher.Infrastructure.Runtime;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 全局设置页（左侧导航的「设置」）。
///
/// 与实例设置分开：这里写的是 &lt;root&gt;\settings.json，实例页写的是 instances.json。
/// 保存策略：改一项存一次（文件只有几 KB，原子写入），不做"保存按钮"——
/// 用户改完直接关窗口也不会丢。
///
/// 只有代理与超时必须在**启动时**读（HttpClient 建好之后改不了），
/// 所以界面上明确写"重启后生效"，而不是假装立刻生效。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly LauncherSettings _startupSettings = new();

    private bool _settingsLoaded;
    private bool _settingsJavaLoading;
    private bool _settingsJavaLoaded;
    private bool _runtimeComponentsLoading;
    private bool _runtimeComponentsLoaded;

    private string _settingsStatus = "";
    private int _settingsSourceIndex;
    private string _settingsMirrorUrl = "";
    private bool _settingsAllowFallback = true;
    private bool _settingsIncludePrereleaseUpdates;
    private int _settingsConcurrency = 12;
    private int _settingsTimeoutSeconds = 600;
    private string _settingsProxy = "";
    private int _settingsDefaultMemory = 4096;
    private bool _settingsDefaultMemoryAuto;
    private int _settingsDefaultIsolationIndex;
    private JavaChoice? _settingsJavaChoice;
    private string _settingsJavaHint = "";
    private string? _settingsDefaultJavaPath;
    private string? _settingsDefaultAccountId;

    private RuntimeChoiceItem? _settingsRuntime;
    private bool _isInstallingRuntime;
    private string _runtimeInstallStatus = "";
    private double _runtimeInstallPercent;
    private bool _runtimeInstallIndeterminate;

    public ICommand ShowSettingsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand OpenSettingsFolderCommand { get; }
    public ICommand InstallRuntimeCommand { get; }

    // ---- 导航 ----

    public bool IsSettingsPage => _navPage == NavPage.Settings;

    public string SettingsNavBackground => IsSettingsPage ? "#2B3A55" : "Transparent";

    public string SettingsNavForeground => IsSettingsPage ? "#DCE4F5" : "#949CA9";

    /// <summary>设置文件路径，显示在页面标题下面，方便用户自己去看/备份。</summary>
    public string SettingsPath => new JsonSettingsStore(_launcher).FilePath;

    public string SettingsStatus
    {
        get => _settingsStatus;
        private set => SetProperty(ref _settingsStatus, value);
    }

    /// <summary>给窗口的"选择文件"失败用：把原因写到设置页底部，而不是弹窗打断。</summary>
    public void ReportSettingsFailure(string message) => SettingsStatus = message;

    // ---- 下载源 ----

    public int SettingsSourceIndex
    {
        get => _settingsSourceIndex;
        set
        {
            if (value is < 0 or > 1) return;
            if (!SetProperty(ref _settingsSourceIndex, value)) return;

            OnPropertyChanged(nameof(SettingsMirrorEnabled));
            OnPropertyChanged(nameof(SettingsSourceText));
            SaveSettings();
        }
    }

    /// <summary>只有选了 BMCLAPI 才会用到镜像地址，但输入框一直可见（方便提前填好自建镜像）。</summary>
    public bool SettingsMirrorEnabled => _settingsSourceIndex == 1;

    public string SettingsSourceText => _settingsSourceIndex == 1
        ? "当前：BMCLAPI 镜像（失败时回退官方源）"
        : "当前：官方源（Mojang）";

    public string SettingsMirrorUrl
    {
        get => _settingsMirrorUrl;
        set
        {
            if (!SetProperty(ref _settingsMirrorUrl, value)) return;
            SaveSettings();
        }
    }

    public bool SettingsAllowFallback
    {
        get => _settingsAllowFallback;
        set
        {
            if (!SetProperty(ref _settingsAllowFallback, value)) return;
            SaveSettings();
        }
    }

    /// <summary>
    /// 检查更新时是否也算 beta / alpha。
    ///
    /// 默认关闭：默认只推荐正式版，避免把不稳定的预发布推给用户；
    /// 有些项目只发预发布（如 26.3 的 Sodium 0.9.3），需要时在这里显式打开。
    /// </summary>
    public bool SettingsIncludePrereleaseUpdates
    {
        get => _settingsIncludePrereleaseUpdates;
        set
        {
            if (!SetProperty(ref _settingsIncludePrereleaseUpdates, value)) return;
            SaveSettings();
        }
    }

    // ---- 下载与网络 ----

    public int SettingsConcurrency
    {
        get => _settingsConcurrency;
        set
        {
            if (value is < 1 or > 64) return;
            if (!SetProperty(ref _settingsConcurrency, value)) return;
            SaveSettings();
        }
    }

    public int SettingsTimeoutSeconds
    {
        get => _settingsTimeoutSeconds;
        set
        {
            if (value is < 30 or > 3600) return;
            if (!SetProperty(ref _settingsTimeoutSeconds, value)) return;

            OnPropertyChanged(nameof(SettingsTimeoutText));
            SaveSettings();
        }
    }

    public string SettingsTimeoutText => $"{_settingsTimeoutSeconds} 秒（约 {_settingsTimeoutSeconds / 60} 分钟）";

    public string SettingsProxy
    {
        get => _settingsProxy;
        set
        {
            if (!SetProperty(ref _settingsProxy, value)) return;

            OnPropertyChanged(nameof(SettingsProxyText));
            SaveSettings();
        }
    }

    public string SettingsProxyText => string.IsNullOrWhiteSpace(_settingsProxy)
        ? "留空 = 直连（不经过代理）"
        : "改完代理需要重启启动器才生效";

    // ---- 默认内存 ----

    public int SettingsDefaultMemory
    {
        get => _settingsDefaultMemory;
        set
        {
            if (value is < 512 or > 65536) return;
            if (!SetProperty(ref _settingsDefaultMemory, value)) return;
            SaveSettings();
        }
    }

    /// <summary>新建实例时默认自动内存（已有实例不受影响）。</summary>
    public bool SettingsDefaultMemoryAuto
    {
        get => _settingsDefaultMemoryAuto;
        set
        {
            if (!SetProperty(ref _settingsDefaultMemoryAuto, value)) return;
            SaveSettings();
        }
    }

    /// <summary>新建实例时用的初始设置：内存取设置页的"默认内存"，其余用默认值。</summary>
    private InstanceSettings NewInstanceSettings() => new()
    {
        MemoryMb = _settingsDefaultMemory,
        AutoMemory = _settingsDefaultMemoryAuto,
    };

    // ---- 新建实例的隔离级别 ----

    /// <summary>两项：版本隔离（默认）/ 完全隔离。不提供"不隔离"。</summary>
    public ObservableCollection<string> SettingsIsolationOptions { get; } = new()
    {
        "版本隔离（共享版本库，推荐）",
        "完全隔离（整包独立，占更多空间）",
    };

    private int? _pendingIsolationIndex;

    /// <summary>
    /// 下拉框选中的值。**选中不等于生效**：先把改动挂起，等用户点「确认更改」。
    /// 隔离级别会改变新实例的目录结构，误触后很难回退，所以给一次确认。
    /// </summary>
    public int SettingsIsolationSelectionIndex
    {
        get => _pendingIsolationIndex ?? _settingsDefaultIsolationIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (clamped == (_pendingIsolationIndex ?? _settingsDefaultIsolationIndex)) return;

            _pendingIsolationIndex = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIsolationChangePending));
            OnPropertyChanged(nameof(IsolationPendingText));
        }
    }

    /// <summary>选中的值与已生效的值不同 → 显示确认条。</summary>
    public bool IsIsolationChangePending =>
        _pendingIsolationIndex is { } pending && pending != _settingsDefaultIsolationIndex;

    public string IsolationPendingText =>
        $"将把新建实例的隔离改为「{SettingsIsolationOptions[_pendingIsolationIndex ?? 0]}」。"
        + "确认后只影响之后新建的实例，**不会移动已有实例的文件**。";

    public ICommand ConfirmIsolationChangeCommand { get; }
    public ICommand CancelIsolationChangeCommand { get; }

    private void ConfirmIsolationChange()
    {
        if (_pendingIsolationIndex is not { } pending) return;

        _settingsDefaultIsolationIndex = pending;
        _pendingIsolationIndex = null;

        OnPropertyChanged(nameof(SettingsIsolationSelectionIndex));
        OnPropertyChanged(nameof(IsIsolationChangePending));
        SettingsStatus = $"新建实例的隔离已改为「{SettingsIsolationOptions[_settingsDefaultIsolationIndex]}」";
        SaveSettings();
    }

    private void CancelIsolationChange()
    {
        _pendingIsolationIndex = null;
        OnPropertyChanged(nameof(SettingsIsolationSelectionIndex));
        OnPropertyChanged(nameof(IsIsolationChangePending));
    }

    /// <summary>新建实例时用的隔离级别。</summary>
    private IsolationLevel NewInstanceIsolation =>
        _settingsDefaultIsolationIndex == 1 ? IsolationLevel.Full : IsolationLevel.Version;
    // ---- 全局默认 Java ----

    public ObservableCollection<JavaChoice> SettingsJavaChoices { get; } = new();

    public JavaChoice? SettingsJavaChoice
    {
        get => _settingsJavaChoice;
        set
        {
            if (!SetProperty(ref _settingsJavaChoice, value)) return;

            UpdateSettingsJavaHint(value);
            SaveSettings();
        }
    }

    public string SettingsJavaHint
    {
        get => _settingsJavaHint;
        private set => SetProperty(ref _settingsJavaHint, value);
    }

    // ---- 缓存占用与清理 ----

    private string _cacheSummaryText = "缓存：统计中…";
    private string _cacheDetailText = "";
    private bool _cacheClearConfirmPending;

    public ICommand ClearCacheCommand { get; }

    /// <summary>一行总览，例如「缓存占用 1.02 GB，其中可清理 1.02 GB」。</summary>
    public string CacheSummaryText
    {
        get => _cacheSummaryText;
        private set => SetProperty(ref _cacheSummaryText, value);
    }

    /// <summary>按大小排的分项明细。</summary>
    public string CacheDetailText
    {
        get => _cacheDetailText;
        private set => SetProperty(ref _cacheDetailText, value);
    }

    /// <summary>统计各缓存目录占用（<root>\data\cache 下）。</summary>
    private void RefreshCacheInfo()
    {
        try
        {
            var cacheRoot = Path.Combine(_launcher.Root, "cache");
            var areas = CacheInventory.Measure(cacheRoot);

            var total = areas.Sum(area => area.Bytes);
            var clearable = areas.Where(area => area.SafeToClear).Sum(area => area.Bytes);

            CacheSummaryText = clearable > 0
                ? $"缓存占用 {CacheInventory.FormatSize(total)}，其中可清理 {CacheInventory.FormatSize(clearable)}"
                : $"缓存占用 {CacheInventory.FormatSize(total)}，暂无可清理项";

            CacheDetailText = string.Join(" · ", areas
                .Where(area => area.Bytes > 0)
                .OrderByDescending(area => area.Bytes)
                .Select(area => $"{area.Label} {CacheInventory.FormatSize(area.Bytes)}"));
        }
        catch (Exception ex)
        {
            CacheSummaryText = $"统计缓存失败：{ex.GetBaseException().Message}";
            CacheDetailText = "";
        }
    }

    /// <summary>清理可再生的缓存（图标 / 整合包解压 / Forge 安装包 / 临时目录）。点两次才执行。</summary>
    private async Task ClearCacheAsync()
    {
        if (!_cacheClearConfirmPending)
        {
            _cacheClearConfirmPending = true;
            CacheSummaryText = "再点一次「清理缓存」确认（只删可再生的缓存，实例与游戏本体不受影响）";
            return;
        }

        _cacheClearConfirmPending = false;
        SettingsStatus = "正在清理缓存…";

        try
        {
            var cacheRoot = Path.Combine(_launcher.Root, "cache");
            var result = await Task.Run(() => CacheInventory.ClearSafe(cacheRoot));

            RefreshCacheInfo();

            var text = $"已清理 {result.Files} 个文件，回收 {CacheInventory.FormatSize(result.Bytes)}";
            if (result.Failures.Count > 0) text += $"（{result.Failures.Count} 个文件被占用，跳过）";
            SettingsStatus = text;
            LauncherLog.Write(_launcher.Root, $"清理缓存：{text}");
        }
        catch (Exception ex)
        {
            SettingsStatus = $"清理缓存失败：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>页面打开时懒加载：枚举 Java 要跑几次 java -version，不该拖慢启动。</summary>
    private async Task EnsureSettingsPageDataAsync()
    {
        await EnsureAccountsLoadedAsync();

        if (!_settingsJavaLoaded) await LoadSettingsJavaChoicesAsync();

        // 运行时清单要联网，失败也不该挡住其它设置
        if (!_runtimeComponentsLoaded) await LoadRuntimeComponentsAsync();

        RefreshCacheInfo();

        RefreshCacheInfo();
    }

    private async Task LoadSettingsJavaChoicesAsync()
    {
        if (_settingsJavaLoading) return;
        _settingsJavaLoading = true;
        SettingsJavaHint = "正在枚举 Java…";

        try
        {
            var built = new List<JavaChoice> { new() { Label = "自动（由游戏版本要求决定）" } };
            var installs = await Task.Run(() => JavaLocator.Discover(_launcher));

            foreach (var install in installs)
            {
                var suffix = install.MajorVersion > 0 ? $"（Java {install.MajorVersion}）" : "";
                var source = install.IsBundled ? "本地" : "系统";
                var name = install.Component ?? DescribeJavaFolder(install.Path);

                built.Add(new JavaChoice
                {
                    Label = $"{source} · {name}{suffix}",
                    Path = install.Path,
                    Component = install.Component,
                    Major = install.MajorVersion,
                });
            }

            // 记录里指定的那个可能不在自动发现的目录里（非常规安装位置）
            if (_settingsDefaultJavaPath is { Length: > 0 } custom
                && File.Exists(custom)
                && built.All(choice => choice.Path is null
                                       || JavaLocator.Normalize(choice.Path) != JavaLocator.Normalize(custom)))
            {
                var major = await Task.Run(() => JavaLocator.ReadMajorVersion(custom) ?? 0);
                built.Add(new JavaChoice
                {
                    Label = $"指定 · {(major > 0 ? $"Java {major} · " : "")}{custom}",
                    Path = custom,
                    Major = major,
                });
            }

            SettingsJavaChoices.Clear();
            foreach (var choice in built) SettingsJavaChoices.Add(choice);

            var selected = SettingsJavaChoices.FirstOrDefault(choice =>
                               _settingsDefaultJavaPath is { Length: > 0 } path
                               && choice.Path is not null
                               && JavaLocator.Normalize(choice.Path) == JavaLocator.Normalize(path))
                           ?? SettingsJavaChoices[0];

            _settingsJavaChoice = selected;
            OnPropertyChanged(nameof(SettingsJavaChoice));
            UpdateSettingsJavaHint(selected);

            _settingsJavaLoaded = true;
        }
        catch (Exception ex)
        {
            SettingsJavaHint = $"枚举 Java 失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            _settingsJavaLoading = false;
        }
    }

    private static string DescribeJavaFolder(string javaExe)
    {
        try
        {
            var bin = Path.GetDirectoryName(javaExe);
            return Path.GetFileName(Path.GetDirectoryName(bin) ?? bin) ?? "java";
        }
        catch
        {
            return "java";
        }
    }

    private void UpdateSettingsJavaHint(JavaChoice? choice)
    {
        if (choice?.Path is not { Length: > 0 } path)
        {
            SettingsJavaHint = "留空/自动：由每个版本自己的元数据决定用哪个 Java；实例里单独指定的仍然优先。";
            return;
        }

        SettingsJavaHint = $"{path}　·　只对「没有单独指定 Java」的实例生效；实例设置里的选择优先。";
    }

    /// <summary>浏览… 选了一个非常规位置的 java.exe。</summary>
    public void SetSettingsCustomJava(string path)    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalized = JavaLocator.Normalize(path);
        var existing = SettingsJavaChoices.FirstOrDefault(choice =>
            choice.Path is not null && JavaLocator.Normalize(choice.Path) == normalized);

        if (existing is null)
        {
            var major = JavaLocator.ReadMajorVersion(path) ?? 0;
            existing = new JavaChoice
            {
                Label = $"指定 · {(major > 0 ? $"Java {major} · " : "")}{path}",
                Path = path,
                Major = major,
            };

            SettingsJavaChoices.Add(existing);
        }

        _settingsJavaLoaded = true;
        SettingsJavaChoice = existing;
    }

    // ---- 预下载 Java 运行时 ----

    public ObservableCollection<RuntimeChoiceItem> RuntimeChoices { get; } = new();

    public RuntimeChoiceItem? SettingsRuntime
    {
        get => _settingsRuntime;
        set
        {
            if (!SetProperty(ref _settingsRuntime, value)) return;
            (InstallRuntimeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsInstallingRuntime
    {
        get => _isInstallingRuntime;
        private set
        {
            if (!SetProperty(ref _isInstallingRuntime, value)) return;
            (InstallRuntimeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string RuntimeInstallStatus
    {
        get => _runtimeInstallStatus;
        private set => SetProperty(ref _runtimeInstallStatus, value);
    }

    public double RuntimeInstallPercent
    {
        get => _runtimeInstallPercent;
        private set => SetProperty(ref _runtimeInstallPercent, value);
    }

    public bool RuntimeInstallIndeterminate
    {
        get => _runtimeInstallIndeterminate;
        private set => SetProperty(ref _runtimeInstallIndeterminate, value);
    }

    private bool CanInstallRuntime => _settingsRuntime is not null && !_isInstallingRuntime;

    /// <summary>下拉顺序：最常用的几个 Java 排在前面，不按字母序把 alpha/beta 顶到最上。</summary>
    private static int RuntimeRank(string component) => component.ToLowerInvariant() switch
    {
        "java-runtime-gamma" => 0,     // Java 17
        "java-runtime-delta" => 1,     // Java 21
        "java-runtime-epsilon" => 2,   // Java 25
        "jre-legacy" => 3,             // Java 8
        _ => 4,
    };

    /// <summary>Mojang 官方运行时清单（windows-x64）。带版本号与"是否已安装"。</summary>
    private async Task LoadRuntimeComponentsAsync()
    {
        if (_runtimeComponentsLoading) return;
        _runtimeComponentsLoading = true;
        RuntimeInstallStatus = "正在读取官方运行时清单…";

        try
        {
            var installer = new MojangRuntimeInstaller(_downloader, _downloader.GetStringAsync);
            var components = await installer.GetComponentsAsync("windows-x64");

            RuntimeChoices.Clear();
            foreach (var component in components
                         .OrderBy(item => RuntimeRank(item.Name))
                         .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var installed = File.Exists(_launcher.JavaExe(component.Name));
                RuntimeChoices.Add(new RuntimeChoiceItem(component, installed));
            }

            var previous = SettingsRuntime?.Component;
            SettingsRuntime = RuntimeChoices.FirstOrDefault(item =>
                                   string.Equals(item.Component, previous, StringComparison.OrdinalIgnoreCase))
                               ?? RuntimeChoices.FirstOrDefault(item => !item.IsInstalled)
                               ?? RuntimeChoices.FirstOrDefault();

            _runtimeComponentsLoaded = RuntimeChoices.Count > 0;
            RuntimeInstallStatus = RuntimeChoices.Count == 0
                ? "清单里没有可用运行时"
                : $"共 {RuntimeChoices.Count} 个运行时组件（已安装 {RuntimeChoices.Count(item => item.IsInstalled)} 个）";
        }
        catch (Exception ex)
        {
            RuntimeInstallStatus = $"读取运行时清单失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            _runtimeComponentsLoading = false;
        }
    }

    private async Task InstallRuntimeAsync()
    {
        if (_settingsRuntime is not { } target || _isInstallingRuntime) return;

        IsInstallingRuntime = true;
        RuntimeInstallIndeterminate = true;
        RuntimeInstallPercent = 0;
        RuntimeInstallStatus = $"正在下载 {target.Component}…";

        try
        {
            var installer = new MojangRuntimeInstaller(_downloader, _downloader.GetStringAsync);
            var progress = new Progress<RuntimeProgress>(report =>
            {
                if (report.Total <= 0) return;

                RuntimeInstallIndeterminate = false;
                RuntimeInstallPercent = report.Completed * 100.0 / report.Total;
                RuntimeInstallStatus = $"正在下载 {target.Component}… {report.Completed}/{report.Total}"
                                       + $"  {report.BytesDownloaded / 1024d / 1024d:N0} MB";
            });

            var result = await installer.InstallAsync(
                "windows-x64", target.Component, _launcher, SettingsConcurrency, progress);

            RuntimeInstallPercent = 100;
            RuntimeInstallStatus = result.JavaExeExists
                ? $"已安装 {target.Component} → {result.JavaExePath}（{result.TotalFiles} 个文件，失败 {result.Failures.Count}）"
                : $"下载完成，但没找到 {result.JavaExePath}";

            LauncherLog.Write(_launcher.Root,
                $"设置页预下载 Java：{target.Component}，{result.TotalFiles} 个文件，失败 {result.Failures.Count}");

            // 运行时变了 → "已安装"状态与 Java 候选都要重算
            _runtimeComponentsLoaded = false;
            await LoadRuntimeComponentsAsync();
            _settingsJavaLoaded = false;
            await LoadSettingsJavaChoicesAsync();
        }
        catch (Exception ex)
        {
            RuntimeInstallStatus = $"安装失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            IsInstallingRuntime = false;
        }
    }

    // ---- 读写 ----

    /// <summary>把读到的设置灌进界面字段（不触发保存）。</summary>
    private void ApplySettingsToFields(LauncherSettings settings)
    {
        var wasLoaded = _settingsLoaded;
        _settingsLoaded = false;

        try
        {
            _settingsSourceIndex = settings.PreferredSource == DownloadSource.Bmclapi ? 1 : 0;
            _settingsMirrorUrl = settings.BmclapiBaseUrl;
            _settingsAllowFallback = settings.AllowSourceFallback;
            _settingsIncludePrereleaseUpdates = settings.IncludePrereleaseUpdates;
            _settingsConcurrency = settings.DownloadConcurrency is >= 1 and <= 64 ? settings.DownloadConcurrency : 12;
            _settingsTimeoutSeconds = settings.HttpTimeoutSeconds is >= 30 and <= 3600 ? settings.HttpTimeoutSeconds : 600;
            _settingsProxy = settings.HttpProxy ?? "";
            _settingsDefaultMemory = settings.DefaultMemoryMb;
            _settingsDefaultMemoryAuto = settings.DefaultMemoryAuto;
            _settingsDefaultIsolationIndex =
                string.Equals(settings.DefaultIsolation, "Full", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _settingsDefaultJavaPath = settings.DefaultJavaPath;
            _settingsDefaultAccountId = settings.DefaultAccountId;

            OnPropertyChanged(nameof(SettingsSourceIndex));
            OnPropertyChanged(nameof(SettingsMirrorEnabled));
            OnPropertyChanged(nameof(SettingsSourceText));
            OnPropertyChanged(nameof(SettingsMirrorUrl));
            OnPropertyChanged(nameof(SettingsAllowFallback));
            OnPropertyChanged(nameof(SettingsIncludePrereleaseUpdates));
            OnPropertyChanged(nameof(SettingsConcurrency));
            OnPropertyChanged(nameof(SettingsTimeoutSeconds));
            OnPropertyChanged(nameof(SettingsTimeoutText));
            OnPropertyChanged(nameof(SettingsProxy));
            OnPropertyChanged(nameof(SettingsProxyText));
            OnPropertyChanged(nameof(SettingsDefaultMemory));
            OnPropertyChanged(nameof(SettingsDefaultMemoryAuto));
            _pendingIsolationIndex = null;
            OnPropertyChanged(nameof(SettingsIsolationSelectionIndex));
            OnPropertyChanged(nameof(IsIsolationChangePending));

            if (_settingsJavaLoaded)
            {
                var selected = SettingsJavaChoices.FirstOrDefault(choice =>
                                   _settingsDefaultJavaPath is { Length: > 0 } path
                                   && choice.Path is not null
                                   && JavaLocator.Normalize(choice.Path) == JavaLocator.Normalize(path))
                               ?? SettingsJavaChoices.FirstOrDefault();

                _settingsJavaChoice = selected;
                OnPropertyChanged(nameof(SettingsJavaChoice));
                UpdateSettingsJavaHint(selected);
            }

            RefreshDefaultAccountSelection();
            RefreshAccountDefaults();
        }
        finally
        {
            _settingsLoaded = wasLoaded;
        }
    }

    private LauncherSettings BuildSettingsFromFields() => new()
    {
        PreferredSource = _settingsSourceIndex == 1 ? DownloadSource.Bmclapi : DownloadSource.Official,
        BmclapiBaseUrl = string.IsNullOrWhiteSpace(_settingsMirrorUrl)
            ? new LauncherSettings().BmclapiBaseUrl
            : _settingsMirrorUrl.Trim().TrimEnd('/'),
        AllowSourceFallback = _settingsAllowFallback,
        IncludePrereleaseUpdates = _settingsIncludePrereleaseUpdates,
        DefaultMemoryMb = _settingsDefaultMemory,
        DefaultMemoryAuto = _settingsDefaultMemoryAuto,
        DefaultIsolation = _settingsDefaultIsolationIndex == 1 ? "Full" : "Version",
        DownloadConcurrency = _settingsConcurrency,
        HttpTimeoutSeconds = _settingsTimeoutSeconds,
        HttpProxy = string.IsNullOrWhiteSpace(_settingsProxy) ? null : _settingsProxy.Trim(),
        // Java 候选还没加载时不要用"空"覆盖掉文件里已有的值
        DefaultJavaPath = _settingsJavaLoaded ? _settingsJavaChoice?.Path : _settingsDefaultJavaPath,
        // 账号候选还没加载时不要用"空"覆盖掉文件里已有的默认账号
        DefaultAccountId = _settingsDefaultAccountId,
        // 语言暂时只有简体中文，保留文件里的值
        Language = _startupSettings.Language,
    };

    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);

    private void SaveSettings() => _ = SaveSettingsAsync();

    /// <summary>供诊断用：等待一次落盘。</summary>
    public Task SaveSettingsPageNowAsync() => SaveSettingsAsync();

    private async Task SaveSettingsAsync()
    {
        if (!_settingsLoaded) return;

        await _settingsSaveLock.WaitAsync();
        try
        {
            await new JsonSettingsStore(_launcher).SaveAsync(BuildSettingsFromFields());
            SettingsStatus = $"已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SettingsStatus = $"保存失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            _settingsSaveLock.Release();
        }
    }

    private async Task ResetSettingsAsync()
    {
        ApplySettingsToFields(new LauncherSettings());
        await SaveSettingsAsync();
        SettingsStatus = "已恢复默认设置";
    }

    private Task OpenSettingsFolder()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        return string.IsNullOrEmpty(directory) ? Task.CompletedTask : OpenDirectoryAsync(directory);
    }
}
