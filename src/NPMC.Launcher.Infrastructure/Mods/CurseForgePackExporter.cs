using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.CurseForge;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>CurseForge 整合包导出结果。</summary>
public sealed record CurseForgeExportResult(
    string TargetPath,
    int FileCount,
    int OverrideCount,
    int UnresolvedCount,
    long Bytes);

/// <summary>
/// 把一个实例导出成 CurseForge 格式的整合包（zip 里是 <c>manifest.json</c> + <c>overrides/</c>）。
///
/// CF 的 manifest 只认 <c>(projectID, fileID)</c>，没有 URL/SHA1，所以：
///   - 每个内容文件先算 CurseForge 指纹（MurmurHash2，Standard，seed=1）；
///   - 批量调 <c>/v1/fingerprints</c> 换出 projectID/fileID，写进 <c>files[]</c>；
///   - 认不出来的文件（自制 / Modrinth 独占 / 已禁用）原样进 <c>overrides/</c>，
///     否则导入方会缺东西；
///   - config、options.txt 等按规则进 <c>overrides/</c>。
///
/// 内容和覆盖层的枚举复用 <see cref="MrpackExporter"/> 的规则，保证两种格式导出的“包含哪些文件”一致。
/// </summary>
public sealed class CurseForgePackExporter
{
    private const int FingerprintBatchSize = 500;

    private readonly CurseForgeClient _curseForge;

    public CurseForgePackExporter(CurseForgeClient curseForge) => _curseForge = curseForge;

    public async Task<CurseForgeExportResult> ExportAsync(
        string gameDirectory,
        string targetPath,
        string name,
        string packVersion,
        string author,
        string minecraftVersion,
        string? loaderName,
        string? loaderVersion,
        MrpackExportOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(gameDirectory))
            throw new DirectoryNotFoundException($"实例目录不存在：{gameDirectory}");

        options ??= new MrpackExportOptions();

        var content = MrpackExporter.CollectContentFiles(gameDirectory, options);
        var overrides = options.BundleExtra
            ? MrpackExporter.CollectOverrideFiles(gameDirectory, options)
            : new List<string>();

        // 已禁用的文件不能进 CF 清单（manifest 没有 disabled 概念），原样放进 overrides
        var candidates = new List<(string RelativePath, string FullPath)>();
        var bundled = new List<string>();

        foreach (var file in content)
        {
            if (file.RelativePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                bundled.Add(file.RelativePath);
            else
                candidates.Add(file);
        }

        progress?.Report($"正在计算 {candidates.Count} 个文件的 CurseForge 指纹…");

        var fingerprints = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                fingerprints[file.RelativePath] = await MurmurHash2
                    .ComputeFileAsync(file.FullPath, MurmurHash2.WhitespaceFilter.Standard, MurmurHash2.DefaultSeed,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 读不了就当认不出来，后面进 overrides
            }
        }

        progress?.Report($"正在向 CurseForge 查询 {fingerprints.Count} 个指纹…");

        var matched = new Dictionary<uint, CurseForgeFile>();

        foreach (var batch in fingerprints.Values.Distinct().Chunk(FingerprintBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lookup = await _curseForge.LookupAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (var match in lookup.Matches) matched[match.Fingerprint] = match.File;
            }
            catch
            {
                // 单批失败不影响其他批；这些文件会进 overrides
            }
        }

        var files = new List<(int ProjectId, int FileId)>();

        foreach (var file in candidates)
        {
            if (fingerprints.TryGetValue(file.RelativePath, out var fingerprint)
                && matched.TryGetValue(fingerprint, out var cfFile))
            {
                files.Add((cfFile.ModId, cfFile.FileId));
            }
            else
            {
                bundled.Add(file.RelativePath);
            }
        }

        var external = options.BundleExtra
            ? MrpackExporter.CollectExternalFiles(options.ExternalPaths)
            : new List<(string Entry, string FullPath)>();

        progress?.Report(
            $"正在写入整合包（{files.Count} 个下载项 + {overrides.Count + bundled.Count} 个覆盖文件）…");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        if (File.Exists(targetPath)) File.Delete(targetPath);

        long bytes;

        using (var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create))
        {
            var manifest = BuildManifestJson(
                name, packVersion, author, minecraftVersion, loaderName, loaderVersion, files);

            var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                var data = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(manifest);
                stream.Write(data, 0, data.Length);
                bytes = data.Length;
            }

            var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var relative in overrides.Concat(bundled))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!packed.Add(relative)) continue;

                var source = Path.Combine(gameDirectory, relative);
                if (!File.Exists(source)) continue;

                archive.CreateEntryFromFile(
                    source,
                    "overrides/" + relative.Replace('\\', '/'),
                    CompressionLevel.Optimal);

                bytes += new FileInfo(source).Length;
            }

            foreach (var (entryName, fullPath) in external)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(fullPath)) continue;

                archive.CreateEntryFromFile(fullPath, entryName, CompressionLevel.Optimal);
                bytes += new FileInfo(fullPath).Length;
            }
        }

        return new CurseForgeExportResult(
            targetPath, files.Count, overrides.Count + bundled.Count, bundled.Count, bytes);
    }

    /// <summary>manifest.json 的内容（抽出来便于测试字段与 loader id 拼法）。</summary>
    public static string BuildManifestJson(
        string name,
        string packVersion,
        string author,
        string minecraftVersion,
        string? loaderName,
        string? loaderVersion,
        IReadOnlyList<(int ProjectId, int FileId)> files)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["minecraft"] = new Dictionary<string, object?>
            {
                ["version"] = minecraftVersion,
                ["modLoaders"] = string.IsNullOrWhiteSpace(loaderName)
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = string.IsNullOrWhiteSpace(loaderVersion)
                                ? loaderName
                                : $"{loaderName}-{loaderVersion}",
                            ["primary"] = true,
                        },
                    },
            },
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = name,
            ["version"] = packVersion,
            ["author"] = author,
            ["files"] = files
                .Select(file => new Dictionary<string, object?>
                {
                    ["projectID"] = file.ProjectId,
                    ["fileID"] = file.FileId,
                    ["required"] = true,
                })
                .ToArray(),
            ["overrides"] = "overrides",
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }
}
