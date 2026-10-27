using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Modpacks;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 整合包页的「在线安装」部分：Modrinth / CurseForge 搜索 → 选版本 →
/// 下载并复用本地安装管线。CurseForge 的整合包 classId 是 4471。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private string _modpackSearchQuery = "";
    private string _modpackSearchStatus = "";
    private bool _isSearchingModpacks;
    private bool _isDownloadingModpack;
    private string _modpackSource = "modrinth";
    private string _modpackProjectId = "";
    private int _selectedModpackSourceIndex;
    private ModpackHitItem? _selectedModpackHit;
    private ModpackVersionItem? _selectedModpackVersion;

    public ObservableCollection<ModpackHitItem> ModpackHits { get; } = new();
    public ObservableCollection<ModpackVersionItem> ModpackVersions { get; } = new();
    public ObservableCollection<string> ModpackSources { get; } = new() { "Modrinth", "CurseForge" };

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

    public int SelectedModpackSourceIndex
    {
        get => _selectedModpackSourceIndex;
        set
        {
            if (!SetProperty(ref _selectedModpackSourceIndex, value)) return;

            _modpackSource = value == 1 ? "curseforge" : "modrinth";
            ModpackHits.Clear();
            ModpackVersions.Clear();
            SelectedModpackHit = null;
            SelectedModpackVersion = null;
            ModpackSearchStatus = value == 1
                ? (IsCurseForgeSearchAvailable
                    ? "CurseForge：输入关键词或留空浏览热门整合包"
                    : "CurseForge 需要 API key：CURSEFORGE_API_KEY 或 %APPDATA%\\NPMC-Launcher\\curseforge.key")
                : "Modrinth：输入关键词或留空浏览热门整合包";

            OnPropertyChanged(nameof(ModpackSourceTitle));
            OnPropertyChanged(nameof(ModpackSourceHint));
        }
    }

    public string ModpackSourceTitle => _selectedModpackSourceIndex == 1
        ? "从 CurseForge 在线安装"
        : "从 Modrinth 在线安装";

    public string ModpackSourceHint => _selectedModpackSourceIndex == 1
        ? "CurseForge 整合包（classId 4471），需要 API key"
        : "Modrinth 整合包";

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
        var curseForge = _selectedModpackSourceIndex == 1;

        if (curseForge && _curseForge is null)
        {
            IsSearchingModpacks = false;
            ModpackSearchStatus =
                "CurseForge 需要 API key：CURSEFORGE_API_KEY 或 %APPDATA%\\NPMC-Launcher\\curseforge.key";
            return;
        }

        ModpackSearchStatus = curseForge ? "正在搜索 CurseForge…" : "正在搜索 Modrinth…";

        try
        {
            var query = ModpackSearchQuery.Trim();
            ModpackHits.Clear();

            if (curseForge)
            {
                var hits = await _curseForge!.SearchAsync(
                    query, gameVersion: null, loaders: Array.Empty<string>(),
                    limit: 20, classId: 4471);

                foreach (var hit in hits)
                    ModpackHits.Add(new ModpackHitItem(hit));

                ModpackSearchStatus = hits.Count == 0
                    ? "CurseForge 没有找到整合包（换个关键词试试）"
                    : $"CurseForge 找到 {hits.Count} 个整合包";
            }
            else
            {
                var hits = await _modrinth.SearchAsync(
                    query, gameVersion: null, loaders: Array.Empty<string>(),
                    limit: 20, projectType: "modpack");

                foreach (var hit in hits)
                    ModpackHits.Add(new ModpackHitItem(hit));

                ModpackSearchStatus = hits.Count == 0
                    ? "没有找到整合包（换个关键词试试）"
                    : $"找到 {hits.Count} 个整合包";
            }

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
            var count = 0;

            if (hit.Source == "curseforge")
            {
                if (_curseForge is null)
                    throw new InvalidOperationException("CurseForge API key 未配置");

                var files = await _curseForge.GetFilesAsync(
                    hit.ModId, gameVersion: "", loaders: Array.Empty<string>());

                foreach (var file in files.Take(100))
                {
                    ModpackVersions.Add(new ModpackVersionItem(file));
                    count++;
                }
            }
            else
            {
                var versions = await _modrinth.GetProjectVersionsAsync(
                    hit.Slug, gameVersion: null, loaders: Array.Empty<string>());

                foreach (var version in versions)
                {
                    ModpackVersions.Add(new ModpackVersionItem(version));
                    count++;
                }
            }

            // 默认选最新的正式版；只有 beta/alpha 时退回第一个
            SelectedModpackVersion = ModpackVersions.FirstOrDefault(version => !version.IsPrerelease)
                                     ?? ModpackVersions.FirstOrDefault();

            ModpackSearchStatus = count == 0
                ? "该整合包没有可下载版本"
                : $"「{hit.Title}」共 {count} 个版本";
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
            ModpackPlan plan;

            if (hit.Source == "curseforge")
            {
                if (version.FileId <= 0)
                    throw new InvalidOperationException("CurseForge 文件 id 无效");

                plan = await PrepareCurseForgeModpackAsync(
                    hit.ModId, version.FileId,
                    new Progress<string>(message => ModpackStatus = message));
            }
            else
            {
                var installer = CreateModpackInstaller();
                plan = await installer.PrepareAsync(
                    hit.Slug, version.Id, Path.Combine(_launcher.Root, "cache", "modpacks"));
            }

            _modpackPlan = plan;

            OnPropertyChanged(nameof(HasModpackPlan));
            OnPropertyChanged(nameof(ModpackTitle));
            OnPropertyChanged(nameof(ModpackSummary));
        OnPropertyChanged(nameof(HasModpackWarnings));
        OnPropertyChanged(nameof(ModpackWarnings));
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

        _modpackSource = "curseforge";
        _modpackProjectId = modId.ToString();

        var preparer = new CurseForgeModpackPreparer(_downloader, new CurseForgeClient(_http, key), _modrinth);

        var plan = await preparer.PrepareAsync(
            modId, fileId, Path.Combine(_launcher.Root, "cache", "cfpacks"), progress);

        _modpackPlan = plan;

        OnPropertyChanged(nameof(HasModpackPlan));
        OnPropertyChanged(nameof(ModpackTitle));
        OnPropertyChanged(nameof(ModpackSummary));
        OnPropertyChanged(nameof(HasModpackWarnings));
        OnPropertyChanged(nameof(ModpackWarnings));
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
