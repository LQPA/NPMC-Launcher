using System.IO.Compression;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Modpacks;

/// <summary>
/// 把 CurseForge 整合包准备成现有 <see cref="ModpackPlan"/>：
///
///   - 下载整合包 zip（里面只有 manifest.json + overrides，没有模组本体）；
///   - 解析 manifest.json，取出 (projectID, fileID) 列表；
///   - 用 CurseForge API 把每个文件换成下载地址（URL 末段就是文件名）；
///   - 组成一份"合成的" Modrinth 风格索引，后面的安装完全复用 ModpackInstaller。
/// </summary>
public sealed class CurseForgeModpackPreparer
{
    private readonly HttpFileDownloader _downloader;
    private readonly CurseForgeClient _curseForge;

    public CurseForgeModpackPreparer(HttpFileDownloader downloader, CurseForgeClient curseForge)
    {
        _downloader = downloader;
        _curseForge = curseForge;
    }

    public async Task<ModpackPlan> PrepareAsync(
        int modId,
        int fileId,
        string cacheDirectory,
        IProgress<string>? progress = null,
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
        var gate = new SemaphoreSlim(4);
        var completed = 0;

        var tasks = manifest.Files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                var url = await _curseForge.TryGetDownloadUrlAsync(
                    file.ProjectId, file.FileId, cancellationToken);

                if (url is null)
                {
                    lock (problems)
                        problems.Add($"{file.ProjectId}/{file.FileId}：作者禁止 API 下载");

                    return;
                }

                var name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
                if (string.IsNullOrWhiteSpace(name)) name = $"{file.FileId}.jar";

                lock (files)
                    files.Add(new ModpackFile($"mods/{name}", "", 0, new[] { url }));
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
            manifest.Name, manifest.Version, index, archivePath, extractRoot, problems);
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
