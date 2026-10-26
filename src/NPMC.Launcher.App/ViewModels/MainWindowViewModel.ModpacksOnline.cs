using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Modpacks;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 整合包页的"在线安装"部分：Modrinth 搜索 → 选版本 → 下载 .mrpack → 复用本地安装管线。
///
/// 只做 Modrinth：CurseForge 需要 API key，而且整合包是另一种 manifest 格式
/// （不是 modrinth.index.json），要单独写解析器；等有 key 再做。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private string _modpackSearchQuery = "";
    private string _modpackSearchStatus = "";
    private bool _isSearchingModpacks;
    private bool _isDownloadingModpack;
    private ModpackHitItem? _selectedModpackHit;
    private ModpackVersionItem? _selectedModpackVersion;

    public ObservableCollection<ModpackHitItem> ModpackHits { get; } = new();
    public ObservableCollection<ModpackVersionItem> ModpackVersions { get; } = new();

    public ICommand SearchModpacksCommand { get; private set; }
    public ICommand DownloadModpackCommand { get; private set; }

    private void InitializeModpackOnlineCommands()
    {
        SearchModpacksCommand = new AsyncRelayCommand(SearchModpacksAsync);
        DownloadModpackCommand = new AsyncRelayCommand(
            DownloadAndInstallModpackAsync,
            () => SelectedModpackHit is not null
                  && SelectedModpackVersion is not null
                  && !IsModpackBusy);
    }

    public string ModpackSearchQuery
    {
        get => _modpackSearchQuery;
        set => SetProperty(ref _modpackSearchQuery, value);
    }

    public string ModpackSearchStatus
    {
        get => _modpackSearchStatus;
        private set => SetProperty(ref _modpackSearchStatus, value);
    }

    public bool IsSearchingModpacks
    {
        get => _isSearchingModpacks;
        private set
        {
            if (!SetProperty(ref _isSearchingModpacks, value)) return;
            OnPropertyChanged(nameof(IsModpackBusy));
        }
    }

    public bool IsDownloadingModpack
    {
        get => _isDownloadingModpack;
        private set
        {
            if (!SetProperty(ref _isDownloadingModpack, value)) return;
            OnPropertyChanged(nameof(IsModpackBusy));
        }
    }

    /// <summary>下载 / 安装都算忙：进度条与按钮可用性都看它。</summary>
    public bool IsModpackBusy => IsInstallingModpack || IsDownloadingModpack;

    public ModpackHitItem? SelectedModpackHit
    {
        get => _selectedModpackHit;
        set
        {
            if (!SetProperty(ref _selectedModpackHit, value)) return;

            NotifyModpackOnlineCommandStates();
            _ = LoadModpackVersionsAsync(value);
        }
    }

    public ModpackVersionItem? SelectedModpackVersion
    {
        get => _selectedModpackVersion;
        set
        {
            if (!SetProperty(ref _selectedModpackVersion, value)) return;
            NotifyModpackOnlineCommandStates();
        }
    }

    private async Task SearchModpacksAsync()
    {
        if (IsSearchingModpacks || IsModpackBusy) return;

        IsSearchingModpacks = true;
        ModpackSearchStatus = "正在搜索 Modrinth…";

        try
        {
            var query = ModpackSearchQuery.Trim();

            var hits = await _modrinth.SearchAsync(
                query, gameVersion: null, loaders: Array.Empty<string>(),
                limit: 20, projectType: "modpack");

            ModpackHits.Clear();

            foreach (var hit in hits)
                ModpackHits.Add(new ModpackHitItem(hit));

            ModpackSearchStatus = hits.Count == 0
                ? "没有找到整合包（换个关键词试试）"
                : $"找到 {hits.Count} 个整合包";

            SelectedModpackHit = ModpackHits.FirstOrDefault();
            NotifyModpackOnlineCommandStates();
        }
        catch (Exception ex)
        {
            ModpackSearchStatus = $"搜索失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            IsSearchingModpacks = false;
        }
    }

    private async Task LoadModpackVersionsAsync(ModpackHitItem? hit)
    {
        ModpackVersions.Clear();
        SelectedModpackVersion = null;

        if (hit is null)
        {
            NotifyModpackOnlineCommandStates();
            return;
        }

        ModpackSearchStatus = $"正在读取「{hit.Title}」的版本…";

        try
        {
            var versions = await _modrinth.GetProjectVersionsAsync(
                hit.Slug, gameVersion: null, loaders: Array.Empty<string>());

            foreach (var version in versions)
                ModpackVersions.Add(new ModpackVersionItem(version));

            // 默认选最新的正式版；只有 beta/alpha 时退回第一个
            SelectedModpackVersion = ModpackVersions.FirstOrDefault(version => !version.IsPrerelease)
                                     ?? ModpackVersions.FirstOrDefault();

            ModpackSearchStatus = versions.Count == 0
                ? "该整合包没有可下载版本"
                : $"「{hit.Title}」共 {versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            ModpackSearchStatus = $"读取版本失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            NotifyModpackOnlineCommandStates();
        }
    }

    /// <summary>下载选中的 .mrpack，然后直接走现有的"本体 → 加载器 → 建实例 → 文件"安装流程。</summary>
    private async Task DownloadAndInstallModpackAsync()
    {
        if (SelectedModpackHit is not { } hit || SelectedModpackVersion is not { } version) return;
        if (IsModpackBusy) return;

        IsDownloadingModpack = true;
        ModpackProgress = "";
        ModpackProgressIndeterminate = true;
        ModpackStatus = $"正在下载「{hit.Title}」{version.VersionNumber}…";

        try
        {
            var installer = CreateModpackInstaller();

            var plan = await installer.PrepareAsync(
                hit.Slug, version.Id, Path.Combine(_launcher.Root, "cache", "modpacks"));

            _modpackPlan = plan;

            OnPropertyChanged(nameof(HasModpackPlan));
            OnPropertyChanged(nameof(ModpackTitle));
            OnPropertyChanged(nameof(ModpackSummary));
            (InstallModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            ModpackStatus = $"下载完成：{plan.Name}（{plan.Index.LoaderText}），开始安装…";
        }
        catch (Exception ex)
        {
            ModpackStatus = $"下载整合包失败：{ex.GetBaseException().Message}";
            return;
        }
        finally
        {
            IsDownloadingModpack = false;
        }

        await InstallModpackAsync();
    }

    /// <summary>诊断/安装用：按 CurseForge 项目 + 文件准备整合包（下载 zip、解析 manifest、换下载地址）。</summary>
    public async Task<ModpackPlan> PrepareCurseForgeModpackAsync(
        int modId, int fileId, IProgress<string>? progress = null)
    {
        if (!CurseForgeClient.TryResolveApiKey(out var key, out var problem) || key is null)
            throw new InvalidOperationException(
                problem ?? "缺少 CurseForge API key（CURSEFORGE_API_KEY 或 %APPDATA%\\NPMC-Launcher\\curseforge.key）");

        var preparer = new CurseForgeModpackPreparer(_downloader, new CurseForgeClient(_http, key));

        var plan = await preparer.PrepareAsync(
            modId, fileId, Path.Combine(_launcher.Root, "cache", "cfpacks"), progress);

        _modpackPlan = plan;

        OnPropertyChanged(nameof(HasModpackPlan));
        OnPropertyChanged(nameof(ModpackTitle));
        OnPropertyChanged(nameof(ModpackSummary));
        (InstallModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

        return plan;
    }

    /// <summary>诊断用：安装已经准备好的整合包（走与界面相同的安装流程）。</summary>
    public Task InstallPreparedModpackAsync() => InstallModpackAsync();
    /// <summary>诊断用：只下载并解析，不安装。</summary>
    public Task<ModpackPlan> PrepareOnlineModpackAsync(string slug, string versionId)
        => CreateModpackInstaller().PrepareAsync(
            slug, versionId, Path.Combine(_launcher.Root, "cache", "modpacks"));

    private void NotifyModpackOnlineCommandStates()
    {
        (SearchModpacksCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
