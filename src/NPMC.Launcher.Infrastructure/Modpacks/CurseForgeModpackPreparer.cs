using System.IO.Compression;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Modrinth;

namespace NPMC.Launcher.Infrastructure.Modpacks;

/// <summary>
/// 把 CurseForge 整合包准备成现有 <see cref="ModpackPlan"/>：
///
///   - 下载整合包 zip（里面只有 manifest.json + overrides，没有模组本体）；
///   - 解析 manifest.json，取出 (projectID, fileID) 列表；
///   - 用 CurseForge API 把每个文件换成下载地址（URL 末段就是文件名）；
///   - 作者禁止 API 下载的文件，回退到 Modrinth 用同名项目补齐（只接受 slug 完全一致）；
///   - 组成一份"合成的" Modrinth 风格索引，后面的安装完全复用 ModpackInstaller。
/// </summary>
public sealed class CurseForgeModpackPreparer
{
    private readonly HttpFileDownloader _downloader;
    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient? _modrinth;

    public CurseForgeModpackPreparer(
        HttpFileDownloader downloader, CurseForgeClient curseForge, ModrinthClient? modrinth = null)
    {
        _downloader = downloader;
        _curseForge = curseForge;
        _modrinth = modrinth;
    }

    public async Task<ModpackPlan> PrepareAsync(
        int modId,
        int fileId,
        string cacheDirectory,
        IProgress<string>? progress = null,
        bool allowModrinthFallback = false,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在获取整合包下载地址…");

        var archiveUrl = await _curseForge.TryGetDownloadUrlAsync(modId, fileId, cancellationToken)
                         ?? throw new InvalidOperationException("作者禁止第三方下载该整合包");

        Directory.CreateDirectory(cacheDirectory);

        var archivePath = Path.Combine(cacheDirectory, $"cf-{modId}-{fileId}.zip");
        progress?.Report("正在下载整合包（含配置覆盖层）…");

        await _downloader.EnsureFromAsync(new[] { archiveUrl }, archivePath, null, cancellationToken);

        CurseForgeModpackManifest manifest;
        string extractRoot;

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json")
                                ?? throw new InvalidDataException("整合包里没有 manifest.json");

            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream))
            {
                manifest = CurseForgeModpackManifestParser.Parse(await reader.ReadToEndAsync());
            }

            if (manifest.Files.Count == 0)
                throw new InvalidDataException("manifest.json 里没有任何文件");

            extractRoot = Path.Combine(cacheDirectory, $"cf-{modId}-{fileId}-overrides");
            if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);

            Directory.CreateDirectory(extractRoot);
            ExtractOverrides(archive, manifest.OverridesDirectory, extractRoot, cancellationToken);
        }

        progress?.Report($"正在解析 {manifest.Files.Count} 个模组的下载地址…");

        var files = new List<ModpackFile>();
        var problems = new List<string>();
        var blockedFiles = new List<BlockedModFile>();
        var gate = new SemaphoreSlim(4);
        var completed = 0;

        var tasks = manifest.Files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                var url = await _curseForge.TryGetDownloadUrlAsync(
                    file.ProjectId, file.FileId, cancellationToken);

                if (url is not null)
                {
                    var name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
                    if (string.IsNullOrWhiteSpace(name)) name = $"{file.FileId}.jar";

                    lock (files)
                        files.Add(new ModpackFile($"mods/{name}", "", 0, new[] { url }));

                    return;
                }

                // 作者禁止 API 下载：默认列给用户手动去 CurseForge 下；
                // 只有显式允许时才用 Modrinth 同项目替代（版本可能不一致）。
                var fallback = allowModrinthFallback
                    ? await TryResolveModrinthAsync(manifest, file, cancellationToken)
                    : null;

                if (fallback is { } replacement)
                {
                    lock (files) files.Add(replacement.File);
                    lock (problems) problems.Add($"已用 Modrinth 替代：{replacement.Note}");
                }
                else
                {
                    var project = await _curseForge.GetProjectAsync(file.ProjectId, cancellationToken);
                    var metadata = await _curseForge.GetFileAsync(
                        file.ProjectId, file.FileId, cancellationToken);

                    var label = project is null
                        ? $"{file.ProjectId}/{file.FileId}"
                        : $"{project.Name}（{project.Slug}）";

                    lock (problems)
                        problems.Add($"只能从 CurseForge 手动下载：{label}");

                    if (metadata is not null)
                    {
                        lock (blockedFiles)
                        {
                            blockedFiles.Add(new BlockedModFile(
                                file.ProjectId, file.FileId,
                                project?.Name ?? label,
                                project?.Slug ?? "",
                                metadata.FileName,
                                metadata.Sha1 ?? ""));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (problems)
                    problems.Add($"{file.ProjectId}/{file.FileId}：{ex.GetBaseException().Message}");
            }
            finally
            {
                gate.Release();

                var done = Interlocked.Increment(ref completed);
                if (done % 25 == 0 || done == manifest.Files.Count)
                    progress?.Report($"已解析 {done}/{manifest.Files.Count} 个文件");
            }
        });

        await Task.WhenAll(tasks);

        if (files.Count == 0)
            throw new InvalidOperationException("没有任何可下载的模组文件");

        var index = new ModpackIndex(
            manifest.Name,
            manifest.Version,
            manifest.GameVersion,
            manifest.LoaderName,
            manifest.LoaderVersion,
            files);

        return new ModpackPlan(
            manifest.Name, manifest.Version, index, archivePath, extractRoot, problems)
        {
            Blocked = blockedFiles,
        };
    }

    /// <summary>
    /// 用 Modrinth 补齐被 CurseForge 作者禁止下载的文件。
    ///
    /// 只接受 **slug 完全一致** 的项目，避免把同名但不同来源的模组混进来；
    /// 版本取该游戏版本 + 加载器下的最新正式版（CF 没有暴露版本号，无法精确对齐）。
    /// </summary>
    private async Task<(ModpackFile File, string Note)?> TryResolveModrinthAsync(
        CurseForgeModpackManifest manifest,
        CurseForgeModpackFile file,
        CancellationToken cancellationToken)
    {
        if (_modrinth is null) return null;

        var project = await _curseForge.GetProjectAsync(file.ProjectId, cancellationToken);
        if (project is null || string.IsNullOrWhiteSpace(project.Slug)) return null;

        var loaders = string.IsNullOrWhiteSpace(manifest.LoaderName)
            ? Array.Empty<string>()
            : new[] { manifest.LoaderName! };

        var hits = await _modrinth.SearchAsync(
            project.Slug, manifest.GameVersion, loaders, limit: 5, projectType: "mod",
            cancellationToken: cancellationToken);

        var hit = hits.FirstOrDefault(candidate =>
                      string.Equals(candidate.Slug, project.Slug, StringComparison.OrdinalIgnoreCase))
                  ?? hits.FirstOrDefault(candidate =>
                      string.Equals(candidate.Title, project.Name, StringComparison.OrdinalIgnoreCase));

        if (hit is null) return null;

        var versions = await _modrinth.GetProjectVersionsAsync(
            hit.Slug, manifest.GameVersion, loaders, cancellationToken);

        var version = versions.FirstOrDefault(candidate => !candidate.IsPrerelease)
                      ?? versions.FirstOrDefault();

        if (version?.PrimaryFile is not { } primary) return null;

        return (new ModpackFile($"mods/{primary.FileName}", primary.Sha1, primary.Size, new[] { primary.Url }),
                $"{project.Name} → Modrinth {hit.Slug} {version.VersionNumber}");
    }

    private static void ExtractOverrides(
        ZipArchive archive, string overridesDirectory, string extractRoot, CancellationToken cancellationToken)
    {
        var prefix = overridesDirectory.TrimEnd('/') + "/";

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var relative = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(extractRoot, relative));

            // 防 zip-slip
            if (!destination.StartsWith(Path.GetFullPath(extractRoot), StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }
}
