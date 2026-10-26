using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Minecraft;

/// <summary>安装进度。界面拿去画进度条，命令行拿去打日志。</summary>
public sealed record MinecraftInstallProgress(
    string Stage,
    int Completed,
    int Total,
    long BytesDownloaded);

/// <summary>安装结果。</summary>
public sealed record MinecraftInstallResult(
    int LibrariesDownloaded,
    int LibrariesSkipped,
    int MirrorHits,
    int OfficialHits,
    int AssetObjects,
    int AssetsDownloaded,
    long AssetBytes,
    IReadOnlyList<string> Failures);

/// <summary>
/// 下载一个游戏版本：版本元数据 → client.jar → 库 → log4j 配置 → assetIndex → 资源对象。
///
/// 为什么抽成服务：这套顺序和"按资源类别回退镜像"的规则原先只写在命令行里，
/// 界面要复用就只能复制一遍；而复制之后，镜像规则一改就会出现两套行为。
///
/// 资源对象是**强制**下载的：缺了它们游戏能启动但没有字体、语言与声音，
/// 那不是"少个可选组件"，是根本没法玩。
/// </summary>
public sealed class MinecraftInstaller
{
    private readonly HttpFileDownloader _downloader;
    private readonly MirrorRouter _router;
    private readonly VersionManifestSource _source;
    private readonly int _concurrency;

    public MinecraftInstaller(
        HttpFileDownloader downloader,
        MirrorRouter router,
        VersionManifestSource source,
        int concurrency = 16)
    {
        _downloader = downloader;
        _router = router;
        _source = source;
        _concurrency = concurrency;
    }

    /// <summary>可用版本列表（新的在前）。</summary>
    public async Task<IReadOnlyList<VersionManifestEntry>> GetVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await _source.GetManifestAsync(ct: cancellationToken).ConfigureAwait(false);
        return manifest.Versions;
    }

    /// <summary>下载并解析版本 JSON（后续启动也要用到它）。</summary>
    public async Task<VersionJson> LoadVersionAsync(
        LauncherPaths paths,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _source.GetManifestAsync(ct: cancellationToken).ConfigureAwait(false);

        var entry = manifest.Find(versionId)
                    ?? throw new InvalidOperationException($"版本清单里没有 {versionId}");

        var jsonPath = paths.VersionJson(versionId);

        await _downloader
            .EnsureFromAsync(Candidates(entry.Url, ResourceKind.VersionMetadata), jsonPath, entry.Sha1)
            .ConfigureAwait(false);

        return VersionJsonSerializer.Parse(await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>执行安装。</summary>
    public async Task<MinecraftInstallResult> InstallAsync(
        LauncherPaths paths,
        string versionId,
        VersionJson version,
        IProgress<MinecraftInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var os = OsInfo.Windows64();

        var mirrorHits = 0;
        var officialHits = 0;
        var failures = new List<string>();

        async Task DownloadAsync(string label, string url, string destination, string? sha1)
        {
            var outcome = await _downloader
                .EnsureFromAsync(Candidates(url, ResourceKind.Library), destination, sha1)
                .ConfigureAwait(false);

            if (outcome.Downloaded)
            {
                if (IsMirrorUrl(outcome.Url)) mirrorHits++;
                else officialHits++;
            }

            // 有失败尝试说明镜像这一路不通、退回了下一个候选
            if (outcome.Attempts.Count > 0)
                failures.Add($"{label}：{outcome.Attempts[0].Error}");
        }

        var effective = version.Libraries
            .Where(library => RuleEvaluator.IsAllowed(library.Rules, os, FeatureSet.ForLaunch(true)))
            .ToList();

        // 1) client.jar
        if (version.Downloads?.Client is { } client)
        {
            progress?.Report(new MinecraftInstallProgress("client.jar", 0, 1, 0));

            await _downloader
                .EnsureFromAsync(Candidates(client.Url, ResourceKind.ClientJar), paths.ClientJar(versionId), client.Sha1)
                .ConfigureAwait(false);
        }

        // 2) 库
        var librariesDownloaded = 0;
        var librariesSkipped = 0;
        var index = 0;

        foreach (var library in effective)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var artifact = library.Downloads?.Artifact;
            if (artifact is null || string.IsNullOrEmpty(artifact.Url))
            {
                librariesSkipped++;
                continue;
            }

            index++;
            progress?.Report(new MinecraftInstallProgress("libraries", index, effective.Count, 0));

            await DownloadAsync(library.Name, artifact.Url, paths.LibraryFile(artifact.Path), artifact.Sha1)
                .ConfigureAwait(false);

            librariesDownloaded++;
        }

        // 3) log4j 配置 —— 该类资源在 BMCLAPI 上是 404，路由表里因此不镜像它
        if (version.Logging?.Client?.File is { } logFile)
        {
            progress?.Report(new MinecraftInstallProgress("log4j", 0, 1, 0));

            await _downloader
                .EnsureFromAsync(
                    Candidates(logFile.Url, ResourceKind.LoggingConfig),
                    paths.LogConfigFile(logFile.Id),
                    logFile.Sha1)
                .ConfigureAwait(false);
        }

        // 4) assetIndex + 资源对象
        var assetObjects = 0;
        var assetsDownloaded = 0;
        long assetBytes = 0;

        if (version.AssetIndex is { } assetIndex)
        {
            progress?.Report(new MinecraftInstallProgress("assetIndex", 0, 1, 0));

            await _downloader
                .EnsureFromAsync(
                    Candidates(assetIndex.Url, ResourceKind.AssetIndex),
                    paths.AssetIndexFile(assetIndex.Id),
                    assetIndex.Sha1)
                .ConfigureAwait(false);

            var assetInstaller = new AssetInstaller(_downloader, _router);

            var assetProgress = progress is null
                ? null
                : new Progress<AssetProgress>(p =>
                    progress.Report(new MinecraftInstallProgress("assets", p.Completed, p.Total, p.BytesDownloaded)));

            var assetResult = await assetInstaller
                .InstallAsync(paths, assetIndex.Id, _concurrency, objectBaseUrl: null, assetProgress, cancellationToken)
                .ConfigureAwait(false);

            assetObjects = assetResult.Total;
            assetsDownloaded = assetResult.Downloaded;
            assetBytes = assetResult.BytesDownloaded;

            failures.AddRange(assetResult.Failures);
        }

        // 5) natives：目录必须存在，旧式声明还要把 dll 解进去（否则 1.7.10 报 no lwjgl）
        Directory.CreateDirectory(paths.NativesDir(versionId));

        var natives = await new NativesInstaller(_downloader)
            .EnsureAsync(paths, versionId, version, os, cancellationToken)
            .ConfigureAwait(false);

        failures.AddRange(natives.Failures);

        return new MinecraftInstallResult(
            librariesDownloaded,
            librariesSkipped,
            mirrorHits,
            officialHits,
            assetObjects,
            assetsDownloaded,
            assetBytes,
            failures);
    }

    private IReadOnlyList<string> Candidates(string url, ResourceKind kind)
        => _router.Resolve(url, kind).Select(target => target.Url).ToList();

    private bool IsMirrorUrl(string url)
        => url.StartsWith(_router.Settings.BmclapiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
