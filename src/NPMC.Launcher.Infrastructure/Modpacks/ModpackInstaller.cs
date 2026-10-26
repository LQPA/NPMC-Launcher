using System.IO.Compression;
using System.Text.Json;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Modrinth;

namespace NPMC.Launcher.Infrastructure.Modpacks;

/// <summary>准备阶段的产物：解析好的清单 + 已解压的覆盖层目录。</summary>
public sealed record ModpackPlan(
    string Name,
    string VersionLabel,
    ModpackIndex Index,
    string ArchivePath,
    string OverridesRoot,
    IReadOnlyList<string> Problems)
{
    /// <summary>只能去 CurseForge 网页手动下载的文件（带文件名与 SHA1，用于校验导入）。</summary>
    public IReadOnlyList<BlockedModFile> Blocked { get; init; } = Array.Empty<BlockedModFile>();
}

/// <summary>安装进度：已完成 / 总数 / 当前文件（相对路径）。</summary>
public sealed record ModpackInstallProgress(int Completed, int Total, string Path)
{
    /// <summary>0-100；总数为 0 时为 null（进度不可知）。</summary>
    public double? Percent => Total > 0 ? Completed * 100.0 / Total : null;
}

/// <summary>安装结果。</summary>
public sealed record ModpackInstallResult(
    int FilesDownloaded,
    int FilesSkipped,
    int OverrideFiles,
    IReadOnlyList<string> Failures);

/// <summary>
/// 安装 Modrinth 整合包（`.mrpack`）。
///
/// 整合包本身**不含模组**，它是一张清单：
///   - `modrinth.index.json`：游戏版本、加载器版本、每个文件的相对路径 + SHA1 + 下载地址
///   - `overrides/`、`client-overrides/`：配置、资源包、光影等直接覆盖进实例目录
///
/// 所以安装分两步：先把清单与覆盖层准备好（联网、解压到临时目录），
/// 再按清单把文件写进实例目录。这样"准备失败"不会留下一个半成品实例。
/// </summary>
public sealed class ModpackInstaller
{
    private const string IndexEntry = "modrinth.index.json";

    private readonly HttpFileDownloader _downloader;
    private readonly ModrinthClient _modrinth;

    public ModpackInstaller(HttpFileDownloader downloader, ModrinthClient modrinth)
    {
        _downloader = downloader;
        _modrinth = modrinth;
    }

    /// <summary>
    /// 从**本地文件**准备（用户自己选的 `.mrpack`，或拖进来的）。
    ///
    /// 与从网站安装的差别只有"文件从哪来"：解压与解析完全一样，
    /// 所以这两条路共用后面的安装逻辑。
    /// </summary>
    public async Task<ModpackPlan> PrepareFromFileAsync(
        string archivePath,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"找不到整合包文件：{archivePath}", archivePath);

        // 本地文件本身可能就是用户的唯一副本，先复制到缓存目录，
        // 后续解压/安装都不会碰原文件。
        Directory.CreateDirectory(cacheDirectory);
        var localCopy = Path.Combine(cacheDirectory, Path.GetFileName(archivePath));

        if (!string.Equals(Path.GetFullPath(localCopy), Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase))
            File.Copy(archivePath, localCopy, overwrite: true);

        return await ExtractAndParseAsync(localCopy, cacheDirectory, versionLabel: "", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 选择整合包版本并下载 `.mrpack`，解压出覆盖层。
    /// </summary>
    /// <param name="versionId">指定版本；为空时取最新正式版。</param>
    public async Task<ModpackPlan> PrepareAsync(
        string projectIdOrSlug,
        string? versionId,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        var versions = await _modrinth
            .GetProjectVersionsAsync(projectIdOrSlug, gameVersion: null, loaders: Array.Empty<string>(), cancellationToken)
            .ConfigureAwait(false);

        if (versions.Count == 0)
            throw new InvalidOperationException("该整合包没有任何版本");

        var chosen = versionId is null
            ? versions.FirstOrDefault(version => !version.IsPrerelease) ?? versions[0]
            : versions.FirstOrDefault(version => version.Id == versionId)
              ?? throw new InvalidOperationException($"整合包没有版本 {versionId}");

        var file = chosen.PrimaryFile
                   ?? throw new InvalidOperationException("该版本没有可下载的整合包文件");

        Directory.CreateDirectory(cacheDirectory);
        var archivePath = Path.Combine(cacheDirectory, file.FileName);

        await _downloader
            .EnsureFromAsync(new[] { file.Url }, archivePath, string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1)
            .ConfigureAwait(false);

        return await ExtractAndParseAsync(archivePath, cacheDirectory, chosen.VersionNumber, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>解压覆盖层并解析清单（下载与本地文件两条路共用）。</summary>
    private static async Task<ModpackPlan> ExtractAndParseAsync(
        string archivePath,
        string cacheDirectory,
        string versionLabel,
        CancellationToken cancellationToken)
    {
        // 解压到"档案同级"的临时目录；先清掉旧的，避免残留上一版覆盖层
        var extractRoot = Path.Combine(cacheDirectory, "extracted", Path.GetFileNameWithoutExtension(archivePath));
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);

        ModpackIndex index;
        var problems = new List<string>();

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var indexEntry = archive.GetEntry(IndexEntry)
                             ?? throw new InvalidDataException($"整合包里没有 {IndexEntry}（不是 Modrinth 格式？）");

            using (var stream = indexEntry.Open())
            using (var reader = new StreamReader(stream))
                index = ModpackIndexParser.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));

            ExtractOverrides(archive, extractRoot, problems, cancellationToken);
        }

        return new ModpackPlan(index.Name, versionLabel, index, archivePath, extractRoot, problems);
    }

    /// <summary>
    /// 把清单里的文件与覆盖层写入实例目录。
    ///
    /// 下载是并发的（默认 8 路）：CF 整合包动辄 400+ 个文件，串行下载会让整包安装
    /// 慢到不可用；已存在且没有 SHA1 校验的文件直接跳过，便于中断后续装。
    /// </summary>
    public async Task<ModpackInstallResult> InstallIntoAsync(
        ModpackPlan plan,
        string instanceDirectory,
        IProgress<ModpackInstallProgress>? progress = null,
        int concurrency = 8,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(instanceDirectory);

        var downloaded = 0;
        var skipped = 0;
        var failures = new List<string>();
        var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 16));
        var completed = 0;
        var total = plan.Index.Files.Count;

        var tasks = plan.Index.Files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = SafeCombine(instanceDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // 没有 SHA1 的条目（CurseForge 整合包都没有）用"文件已存在且非空"当作已完成，
                // 这样中断后重跑不会把已经下好的几百个模组再下一遍。
                if (string.IsNullOrWhiteSpace(file.Sha1)
                    && File.Exists(destination)
                    && new FileInfo(destination).Length > 0)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var outcome = await _downloader
                    .EnsureFromAsync(file.Downloads, destination,
                        string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Downloaded) Interlocked.Increment(ref downloaded);
                else Interlocked.Increment(ref skipped);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add($"{file.Path}：{ex.GetBaseException().Message}");
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ModpackInstallProgress(done, total, file.Path));
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // 覆盖层最后放：它可能包含与清单同名的配置，应该以整合包作者的版本为准
        var overrides = CopyDirectory(plan.OverridesRoot, instanceDirectory);

        return new ModpackInstallResult(downloaded, skipped, overrides, failures);
    }

    /// <summary>解压 overrides 与 client-overrides 到临时目录。</summary>
    private static void ExtractOverrides(
        ZipArchive archive,
        string extractRoot,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 只取两类覆盖层；index 与其余条目忽略
            var relative = entry.FullName.Replace('\\', '/');
            var isOverride = relative.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)
                             || relative.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase);

            if (!isOverride || relative.EndsWith('/')) continue;

            var trimmed = relative.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)
                ? relative["overrides/".Length..]
                : relative["client-overrides/".Length..];

            try
            {
                var path = SafeCombine(extractRoot, trimmed);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
            catch (Exception ex)
            {
                problems.Add($"覆盖层 {trimmed}：{ex.GetBaseException().Message}");
            }
        }
    }

    /// <summary>递归复制目录，返回复制的文件数。</summary>
    private static int CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return 0;

        var count = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = SafeCombine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 拼路径并确认没有跑到目标目录外面（zip 条目与清单路径都来自外部，不能直接信）。
    /// </summary>
    private static string SafeCombine(string root, string relative)
    {
        var normalized = ModpackIndexParser.NormalizePath(relative);
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        var rootFull = Path.GetFullPath(root);

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路径越界：{relative}");

        return full;
    }
}
