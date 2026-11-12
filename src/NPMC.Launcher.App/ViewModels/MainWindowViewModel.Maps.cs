using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Core.Worlds;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Modrinth;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 下载页「地图」区域：从 CurseForge 的 Worlds 分区（classId 17）搜索存档，
/// 要求用户选一个实例，只把**版本兼容**的实例排在前面可选，下载后解压到
/// <c>saves/&lt;地图名&gt;/</c>。
///
/// 为什么只有 CurseForge：
///   Modrinth 的 project_type 只有 mod / modpack / resourcepack / shader / plugin /
///   datapack / minecraft_java_server 七种，**没有存档地图**（实测 project_type:world 命中 0）。
///
/// 三个实测结论决定了这里的设计：
///   1. 地图的 gameVersions 是纯版本号（1.20.1 / 26.2 / 26.3-snapshot），
///      不像模组带 Client/Forge/Server 标记，所以能干净地跟实例版本比；
///   2. 相当一部分地图 allowModDistribution = false，拿不到下载地址 ——
///      这类必须在列表里明确标出来，否则用户点下载只会得到一个看不懂的失败；
///   3. 文件是 .zip 存档包，解压到 saves/ 下，不是丢进某个内容目录。
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>CurseForge 的 Worlds 分区（Worlds 及其 7 个子分类的父级）。</summary>
    private const int CurseForgeWorldsClassId = 17;

    private string _mapSearchQuery = "";
    private string _mapSearchStatus = "";
    private bool _isSearchingMaps;
    private bool _isInstallingMap;
    private MapHitItem? _selectedMapHit;
    private MapVersionItem? _selectedMapVersion;
    private MapTargetInstanceItem? _selectedMapTarget;

    public ObservableCollection<MapHitItem> MapHits { get; } = new();
    public ObservableCollection<MapVersionItem> MapVersions { get; } = new();
    public ObservableCollection<MapTargetInstanceItem> MapTargetInstances { get; } = new();

    public ICommand SearchMapsCommand { get; private set; } = null!;
    public ICommand InstallMapCommand { get; private set; } = null!;
    public ICommand OpenMapPageCommand { get; private set; } = null!;

    private void InitializeMapCommands()
    {
        SearchMapsCommand = new AsyncRelayCommand(SearchMapsAsync);
        InstallMapCommand = new AsyncRelayCommand(InstallMapAsync, () => CanInstallMap);
        OpenMapPageCommand = new RelayCommand(OpenMapPage);

        MapSearchStatus = IsCurseForgeSearchAvailable
            ? "输入关键词搜索存档（留空按下载量浏览热门地图）"
            : "地图来自 CurseForge，需要 API key：在「设置 → 下载源」里填写";
    }

    /// <summary>作者禁止第三方下载时，把用户送到 CurseForge 项目页手动下载。</summary>
    private void OpenMapPage()
    {
        var url = SelectedMapWebUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MapSearchStatus = $"无法打开浏览器：{ex.GetBaseException().Message}";
        }
    }

    public string MapSearchQuery
    {
        get => _mapSearchQuery;
        set => SetProperty(ref _mapSearchQuery, value);
    }

    public string MapSearchStatus
    {
        get => _mapSearchStatus;
        private set => SetProperty(ref _mapSearchStatus, value);
    }

    public bool IsSearchingMaps
    {
        get => _isSearchingMaps;
        private set
        {
            if (!SetProperty(ref _isSearchingMaps, value)) return;
            OnPropertyChanged(nameof(IsMapBusy));
        }
    }

    public bool IsInstallingMap
    {
        get => _isInstallingMap;
        private set
        {
            if (!SetProperty(ref _isInstallingMap, value)) return;
            OnPropertyChanged(nameof(IsMapBusy));
            RaiseMapCommandStates();
        }
    }

    public bool IsMapBusy => IsSearchingMaps || IsInstallingMap;

    public bool IsMapSearchAvailable => IsCurseForgeSearchAvailable;

    public MapHitItem? SelectedMapHit
    {
        get => _selectedMapHit;
        set
        {
            if (!SetProperty(ref _selectedMapHit, value)) return;
            _ = LoadMapVersionsAsync(value);
        }
    }

    public MapVersionItem? SelectedMapVersion
    {
        get => _selectedMapVersion;
        set
        {
            if (!SetProperty(ref _selectedMapVersion, value)) return;

            // 换版本后地图支持的版本变了，重新给实例排序
            RebuildMapTargets();
            RaiseMapCommandStates();
        }
    }

    public MapTargetInstanceItem? SelectedMapTarget
    {
        get => _selectedMapTarget;
        set
        {
            if (!SetProperty(ref _selectedMapTarget, value)) return;
            RaiseMapCommandStates();
        }
    }

    /// <summary>选中的地图支持哪些版本（给人看的一句话）。</summary>
    public string SelectedMapSupportText =>
        SelectedMapVersion is { } version
            ? MapVersionMatch.DescribeMatch(version.GameVersions)
            : "";

    public bool HasMapHits => MapHits.Count > 0;

    public bool HasMapVersions => MapVersions.Count > 0;

    /// <summary>界面上的「下载并解压到实例」按钮是否可点（与命令的 CanExecute 保持一致）。</summary>
    public bool CanDownloadMap => CanInstallMap;

    /// <summary>选中地图的网页地址（曲法/CurseForge 项目页）；没有时为 null。</summary>
    public string? SelectedMapWebUrl => SelectedMapHit?.WebUrl;

    private bool CanInstallMap =>
        SelectedMapHit is not null
        && SelectedMapVersion is not null
        && SelectedMapTarget is not null
        && SelectedMapTarget.CanInstall
        && !IsMapBusy;

    private void RaiseMapCommandStates()
    {
        (InstallMapCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedMapSupportText));
        OnPropertyChanged(nameof(CanDownloadMap));
        OnPropertyChanged(nameof(SelectedMapVersion));
        OnPropertyChanged(nameof(SelectedMapHit));
        OnPropertyChanged(nameof(SelectedMapWebUrl));
    }

    private async Task SearchMapsAsync()
    {
        if (IsSearchingMaps || IsInstallingMap) return;

        if (_curseForge is null)
        {
            MapSearchStatus = "地图来自 CurseForge，需要 API key：在「设置 → 下载源」里填写";
            return;
        }

        IsSearchingMaps = true;
        MapSearchStatus = "正在搜索 CurseForge 存档…";

        try
        {
            MapHits.Clear();
            MapVersions.Clear();
            SelectedMapHit = null;
            SelectedMapVersion = null;

            var hits = await _curseForge.SearchAsync(
                MapSearchQuery.Trim(), gameVersion: null, loaders: Array.Empty<string>(),
                limit: 20, classId: CurseForgeWorldsClassId);

            foreach (var hit in hits)
                MapHits.Add(new MapHitItem(hit));

            OnPropertyChanged(nameof(HasMapHits));

            MapSearchStatus = hits.Count == 0
                ? "没有找到存档（换个关键词试试）"
                : $"找到 {hits.Count} 个存档";

            SelectedMapHit = MapHits.FirstOrDefault();
        }
        catch (Exception ex)
        {
            MapSearchStatus = $"搜索失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            IsSearchingMaps = false;
        }
    }

    private async Task LoadMapVersionsAsync(MapHitItem? hit)
    {
        MapVersions.Clear();
        SelectedMapVersion = null;

        if (hit is null || _curseForge is null)
        {
            OnPropertyChanged(nameof(HasMapVersions));
            RaiseMapCommandStates();
            return;
        }

        MapSearchStatus = $"正在读取「{hit.Title}」的文件…";

        try
        {
            var files = await _curseForge.GetFilesAsync(hit.ModId, gameVersion: "", loaders: Array.Empty<string>());

            foreach (var file in files.Take(50))
                MapVersions.Add(new MapVersionItem(file));

            OnPropertyChanged(nameof(HasMapVersions));

            // 默认选最新正式版；全是 beta 时退回第一个
            SelectedMapVersion = MapVersions.FirstOrDefault(version => !version.IsPrerelease)
                                 ?? MapVersions.FirstOrDefault();

            MapSearchStatus = MapVersions.Count == 0
                ? "该地图没有可下载文件"
                : $"「{hit.Title}」共 {MapVersions.Count} 个文件";
        }
        catch (Exception ex)
        {
            MapSearchStatus = $"读取文件失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            RaiseMapCommandStates();
        }
    }

    /// <summary>
    /// 按"与这个地图版本是否兼容"给实例排序：完全匹配 → 同版本段 → 不匹配。
    ///
    /// 用户要求"只显示支持的实例版本"，这里做成**兼容的排前面并高亮，不兼容的留在末尾且不可选**：
    /// 直接隐藏会让用户以为自己的实例丢了，而不可选 + 一句说明更清楚。
    /// </summary>
    private void RebuildMapTargets()
    {
        MapTargetInstances.Clear();
        SelectedMapTarget = null;

        var mapVersions = SelectedMapVersion?.GameVersions ?? Array.Empty<string>();

        // 每个实例的 MC 版本：先读版本 JSON 的 inheritsFrom（四家加载器都准确写出），
        // 读不到再退回按名字解析。批量算一次，避免排序时反复 IO。
        var gameVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var instance in Instances)
        {
            if (gameVersions.ContainsKey(instance.VersionId)) continue;

            gameVersions[instance.VersionId] =
                LoaderVersionName.ResolveGameVersion(_launcher, instance.VersionId);
        }

        var ordered = Instances
            .Select(instance => new MapTargetInstanceItem(instance, mapVersions, gameVersions[instance.VersionId]))
            .OrderBy(item => item.MatchLevel)
            .ThenBy(item => item.Instance.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var item in ordered)
            MapTargetInstances.Add(item);

        SelectedMapTarget = MapTargetInstances.FirstOrDefault(item => item.CanInstall);
        RaiseMapCommandStates();
    }

    /// <summary>下载选中的存档并解压进目标实例的 saves/。</summary>
    private async Task InstallMapAsync()
    {
        if (SelectedMapHit is not { } hit || SelectedMapVersion is not { } version
            || SelectedMapTarget is not { } target || !target.CanInstall)
        {
            return;
        }

        if (IsMapBusy) return;

        IsInstallingMap = true;
        MapSearchStatus = $"正在下载「{hit.Title}」…";

        var downloadDirectory = Path.Combine(_launcher.Root, "cache", "maps");
        Directory.CreateDirectory(downloadDirectory);

        try
        {
            var url = version.DownloadUrl;

            // 搜索结果里的 downloadUrl 有时为空：FileId 还在的话再申请一次
            if (string.IsNullOrWhiteSpace(url) && version.FileId > 0 && _curseForge is not null)
                url = await _curseForge.TryGetDownloadUrlAsync(hit.ModId, version.FileId);

            if (string.IsNullOrWhiteSpace(url))
            {
                MapSearchStatus = version.PinnedByAuthor
                    ? "作者禁止第三方下载这个文件：请点「打开网页」到 CurseForge 手动下载，再拖进对应实例的 saves 文件夹"
                    : "拿不到下载地址：请点「打开网页」到 CurseForge 手动下载";
                return;
            }

            var archivePath = Path.Combine(downloadDirectory, version.SafeFileName);
            await _downloader
                .EnsureFromAsync(new[] { url }, archivePath, sha1: null);

            MapSearchStatus = $"解压到实例「{target.Instance.Name}」的 saves 目录…";

            var saves = target.Instance.SavesDirectory;
            var result = await MapExtractor.ExtractAsync(archivePath, saves, hit.Title);

            MapSearchStatus = result.Succeeded
                ? $"已安装到实例「{target.Instance.Name}」：{result.WorldName}（{result.ExtractedFiles} 个文件）"
                : $"已解压 {result.WorldName}，但有 {result.Problems.Count} 个文件失败（见 logs/launcher.log）";

            LauncherLog.Write(
                _launcher.Root,
                $"地图安装：{hit.Title} → {target.Instance.Name}/{result.WorldName}，{result.ExtractedFiles} 个文件，失败 {result.Problems.Count}");
        }
        catch (Exception ex)
        {
            MapSearchStatus = $"安装失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            IsInstallingMap = false;
        }
    }
}

/// <summary>搜索结果里的一张地图。</summary>
public sealed class MapHitItem
{
    public MapHitItem(CurseForgeSearchHit hit)
    {
        ModId = hit.ModId;
        Title = hit.DisplayName;
        Summary = hit.Summary;
        DownloadCount = hit.DownloadCount;
        LogoUrl = hit.LogoUrl;
        WebUrl = string.IsNullOrWhiteSpace(hit.Slug)
            ? ""
            : $"https://www.curseforge.com/minecraft/worlds/{hit.Slug}";
    }

    public int ModId { get; }
    public string Title { get; }
    public string Summary { get; }
    public long DownloadCount { get; }
    public string LogoUrl { get; }
    public string WebUrl { get; }

    public string DownloadCountText => DownloadCount switch
    {
        >= 1_000_000 => $"{DownloadCount / 1_000_000.0:0.#}M 次下载",
        >= 1_000 => $"{DownloadCount / 1_000.0:0.#}k 次下载",
        _ => $"{DownloadCount} 次下载",
    };

    public override string ToString() => Title;
}

/// <summary>地图的一个文件版本。</summary>
public sealed class MapVersionItem
{
    public MapVersionItem(CurseForgeFile file)
    {
        FileId = file.FileId;
        FileName = file.FileName;
        DownloadUrl = file.DownloadUrl;
        GameVersions = file.GameVersions;
        IsPrerelease = file.ReleaseType != 1;
        SizeInBytes = file.FileLength;
    }

    public int FileId { get; }
    public string FileName { get; }
    public string? DownloadUrl { get; }
    public IReadOnlyList<string> GameVersions { get; }
    public bool IsPrerelease { get; }
    public long SizeInBytes { get; }

    /// <summary>作者禁止第三方下载：列表里拿不到地址。</summary>
    public bool PinnedByAuthor => string.IsNullOrWhiteSpace(DownloadUrl);

    public string SafeFileName
    {
        get
        {
            var cleaned = FileName;
            foreach (var invalid in Path.GetInvalidFileNameChars())
                cleaned = cleaned.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(cleaned) ? "map.zip" : cleaned;
        }
    }

    public string Label => $"{FileName} · {MapVersionMatch.DescribeMatch(GameVersions)}"
                           + $" · {SizeInBytes / 1024.0 / 1024.0:0.#} MB";

    public override string ToString() => Label;
}

/// <summary>可作为地图安装目标的实例，附带与地图的兼容程度。</summary>
public sealed class MapTargetInstanceItem
{
    public MapTargetInstanceItem(
        InstanceListItem instance,
        IReadOnlyList<string> mapVersions,
        string? resolvedGameVersion = null)
    {
        Instance = instance;

        // 优先用调用方解析好的 MC 版本（来自 inheritsFrom，四家加载器都准确）；
        // 没传时退回按版本名解析。
        var minecraft = string.IsNullOrWhiteSpace(resolvedGameVersion)
            ? MapVersionMatch.MinecraftVersionOf(instance.VersionId)
            : resolvedGameVersion!;

        MinecraftVersion = minecraft;

        // 版本名里可能写的是加载器 profile，界面显示 MC 版本更有意义
        var version = minecraft.Length > 0
            ? minecraft
            : (string.IsNullOrWhiteSpace(instance.VersionId) ? "未知版本" : instance.VersionId);

        MatchLevel = minecraft.Length == 0
            ? MapMatchLevel.None
            : MapVersionMatch.Match(mapVersions, minecraft);

        MatchText = MatchLevel switch
        {
            MapMatchLevel.Exact => $"完全匹配 · MC {version}",
            MapMatchLevel.SameSeries => $"同版本段可用 · MC {version}",
            _ when minecraft.Length == 0 => $"无法判断版本（实例名 {version}）",
            _ => $"版本不匹配 · MC {version}",
        };
    }

    public InstanceListItem Instance { get; }
    public MapMatchLevel MatchLevel { get; }

    /// <summary>解析出来的 Minecraft 版本（空表示判断不了）。</summary>
    public string MinecraftVersion { get; }

    /// <summary>界面上给用户看的兼容性说明。</summary>
    public string MatchText { get; }

    /// <summary>不兼容的实例不允许作为目标（避免把存档装进一定打不开的实例）。</summary>
    public bool CanInstall => MatchLevel != MapMatchLevel.None;

    public override string ToString() => $"{Instance.Name} — {MatchText}";
}
