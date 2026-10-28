using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.Hashing;
using NPMC.Launcher.Infrastructure.Modrinth;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>一个写进 <c>modrinth.index.json</c> 的文件（能在 Modrinth 上找到下载源）。</summary>
public sealed record MrpackFileEntry(
    string Path,
    string Sha1,
    string Sha512,
    long Size,
    string DownloadUrl,
    bool ClientSide,
    bool ServerSide);

/// <summary>导出结果：多少文件走了下载、多少进了 overrides、多少没能识别。</summary>
public sealed record MrpackExportResult(
    string TargetPath,
    int FileCount,
    int OverrideCount,
    int UnresolvedCount,
    long Bytes);

/// <summary>
/// 导出时勾选的内容（对应 PCL 的「导出内容列表」）。
///
/// 注意 <see cref="BundleExtra"/> 的语义：关掉它只生成下载清单（体积最小），
/// 但 overrides 里的东西都会丢，导入方拿到的是"干净但没配置"的包。
/// </summary>
public sealed record MrpackExportOptions
{
    public bool IncludeMods { get; init; } = true;
    public bool IncludeResourcePacks { get; init; } = true;
    public bool IncludeShaderPacks { get; init; } = true;

    /// <summary>把 overrides 打包进去；关掉就只写下载清单。</summary>
    public bool BundleExtra { get; init; } = true;

    /// <summary>
    /// overrides 的规则（`export-rules.txt` 的前半段）：相对实例目录的包含/排除规则，
    /// 语法见 <see cref="ExportRules"/>。
    /// </summary>
    public IReadOnlyList<string> Rules { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 规则文件 `=====` 之后的绝对路径：原样放到压缩包根目录（PCL 的"额外文件"）。
    /// 结尾带分隔符表示文件夹。
    /// </summary>
    public IReadOnlyList<string> ExternalPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 把一个实例导出成 Modrinth 整合包（<c>.mrpack</c>）。
///
/// 规则（见 Modrinth 的 index 规范）：
///   - 能在 Modrinth 上按哈希认出来的 jar/zip → 写进 <c>files[]</c>，整合包只记下载地址与哈希，
///     别人导入时由启动器重新下载（整合包因此很小）；
///   - 认不出来的文件（自制、非 Modrinth 发布）→ 原样塞进 <c>overrides/</c>，
///     导入后直接落盘，否则整合包会缺东西；
///   - 配置类目录（config / defaultconfigs）与 options.txt 等也进 <c>overrides/</c>；
///   - logs / crash-reports / saves 永远不导出（前者是噪音，后者是玩家的世界）。
/// </summary>
public sealed class MrpackExporter
{
    public const string IndexFileName = "modrinth.index.json";

    /// <summary>会被当作"可下载内容"去 Modrinth 反查的目录（含已禁用的 *.xxx.disabled）。</summary>
    private static readonly (string Directory, string[] Patterns, bool ClientSide, bool ServerSide)[] ContentDirectories =
    {
        ("mods", new[] { "*.jar", "*.jar.disabled" }, true, true),
        ("resourcepacks", new[] { "*.zip", "*.zip.disabled" }, true, false),
        ("shaderpacks", new[] { "*.zip", "*.zip.disabled" }, true, false),
    };

    /// <summary>
    /// 不进导出的子目录。`mods/.trash` 是被删除模组的回收站：哈希能认出来，
    /// 但把"删掉的东西"打进整合包显然不是用户想要的。
    /// </summary>
    private static readonly string[] IgnoredSegments = { ".trash" };

    private readonly ModrinthClient _modrinth;

    public MrpackExporter(ModrinthClient modrinth) => _modrinth = modrinth;

    public async Task<MrpackExportResult> ExportAsync(
        string gameDirectory,
        string targetPath,
        string name,
        string packVersion,
        string summary,
        string minecraftVersion,
        string? loaderKey,
        string? loaderVersion,
        MrpackExportOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(gameDirectory))
            throw new DirectoryNotFoundException($"实例目录不存在：{gameDirectory}");

        options ??= new MrpackExportOptions();

        var content = CollectContentFiles(gameDirectory, options);
        var overrides = options.BundleExtra ? CollectOverrideFiles(gameDirectory, options) : new List<string>();

        progress?.Report($"正在计算文件哈希（{content.Count} 个内容文件）…");
        var hashes = await HashFilesAsync(content, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report($"正在向 Modrinth 查询 {content.Count} 个文件的下载源…");
        var matched = await LookupAsync(content, hashes, progress, cancellationToken).ConfigureAwait(false);

        var entries = new List<MrpackFileEntry>();
        var unresolved = new List<string>();

        foreach (var file in content)
        {
            if (matched.TryGetValue(file.RelativePath, out var entry)) entries.Add(entry);
            else unresolved.Add(file.RelativePath);
        }

        // 只写下载清单时，认不出来的文件不打包也不记录（结果里会报数量，界面提示会说明）
        var bundled = options.BundleExtra ? unresolved : new List<string>();

        // 规则文件 `=====` 之后的绝对路径：直接放到压缩包根目录（PCL 的"额外文件"）
        var external = options.BundleExtra ? CollectExternalFiles(options.ExternalPaths) : new List<(string Entry, string FullPath)>();

        progress?.Report(
            $"正在写入整合包（{entries.Count} 个下载项 + {overrides.Count + bundled.Count + external.Count} 个打包文件）…");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        if (File.Exists(targetPath)) File.Delete(targetPath);

        long bytes;

        using (var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create))
        {
            bytes = WriteIndex(archive, name, packVersion, summary, minecraftVersion, loaderKey, loaderVersion, entries);

            foreach (var relative in overrides)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = Path.Combine(gameDirectory, relative);
                archive.CreateEntryFromFile(
                    source,
                    "overrides/" + relative.Replace('\\', '/'),
                    CompressionLevel.Optimal);

                bytes += new FileInfo(source).Length;
            }

            // 认不出来的内容文件也进 overrides：否则整合包导入后少了这些模组
            foreach (var relative in bundled)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = Path.Combine(gameDirectory, relative);
                archive.CreateEntryFromFile(
                    source,
                    "overrides/" + relative.Replace('\\', '/'),
                    CompressionLevel.Optimal);

                bytes += new FileInfo(source).Length;
            }

            foreach (var (entryName, fullPath) in external)
            {
                cancellationToken.ThrowIfCancellationRequested();

                archive.CreateEntryFromFile(fullPath, entryName, CompressionLevel.Optimal);
                bytes += new FileInfo(fullPath).Length;
            }
        }

        return new MrpackExportResult(targetPath, entries.Count, overrides.Count + external.Count, unresolved.Count, bytes);
    }

    /// <summary>
    /// 绝对路径的额外文件：文件按文件名放到包根，目录则保留目录名层级。
    /// 不存在的路径直接跳过（用户可能手写过期路径）。
    /// </summary>
    private static List<(string Entry, string FullPath)> CollectExternalFiles(IReadOnlyList<string> paths)
    {
        var result = new List<(string, string)>();

        foreach (var raw in paths)
        {
            var path = raw.Trim();
            if (path.Length == 0) continue;

            var isDirectory = path.EndsWith('\\') || path.EndsWith('/');
            var trimmed = path.TrimEnd('\\', '/');

            try
            {
                if (File.Exists(trimmed))
                {
                    result.Add((Path.GetFileName(trimmed), trimmed));
                }
                else if (Directory.Exists(trimmed))
                {
                    var name = Path.GetFileName(trimmed);

                    foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(trimmed, file).Replace('\\', '/');
                        result.Add((name + "/" + relative, file));
                    }
                }
                else if (isDirectory)
                {
                    // 目录不存在：忽略
                }
            }
            catch
            {
                // 读不到的路径跳过，别让一个坏路径毁掉整次导出
            }
        }

        return result;
    }

    private static List<(string RelativePath, string FullPath)> CollectContentFiles(
        string gameDirectory,
        MrpackExportOptions options)
    {
        var files = new List<(string, string)>();

        foreach (var (directory, patterns, _, _) in ContentDirectories)
        {
            var included = directory switch
            {
                "mods" => options.IncludeMods,
                "resourcepacks" => options.IncludeResourcePacks,
                "shaderpacks" => options.IncludeShaderPacks,
                _ => false,
            };

            if (!included) continue;

            var root = Path.Combine(gameDirectory, directory);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(gameDirectory, file);

                if (IsIgnored(relative)) continue;
                if (!patterns.Any(pattern => MatchesPattern(Path.GetFileName(file), pattern))) continue;

                files.Add((relative, file));
            }
        }

        // 排序只为让输出稳定（导出两次得到相同的 index 顺序）
        files.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));
        return files;
    }

    /// <summary>路径里带回收站等不该导出的段。</summary>
    private static bool IsIgnored(string relativePath) =>
        relativePath
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => IgnoredSegments.Contains(part, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 把"用户输入的相对路径"解析成实例内的绝对路径；越界（绝对路径、`..`、别的盘）
    /// 一律返回 null —— 导出不该能把任意系统目录塞进整合包。
    /// </summary>
    public static string? ResolveInside(string gameDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        var trimmed = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(trimmed)) return null;
        if (trimmed.Split(Path.DirectorySeparatorChar).Contains("..")) return null;

        try
        {
            var root = Path.GetFullPath(gameDirectory);
            var full = Path.GetFullPath(Path.Combine(root, trimmed));

            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>简单的通配符匹配（`*.jar` / `*.jar.disabled`），够用且不引第三方包。</summary>
    private static bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern.StartsWith('*')) return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectOverrideFiles(string gameDirectory, MrpackExportOptions options)
    {
        var overrides = new List<string>();

        // 整个实例目录按规则走：content 目录（mods/资源包/光影）属于下载清单，
        // 认不出的那些另有处理，所以这里跳过，避免同一文件进两处。
        foreach (var file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(gameDirectory, file);

            if (IsIgnored(relative)) continue;
            if (IsContentPath(relative)) continue;

            var normalized = relative.Replace('\\', '/');
            if (!ExportRules.IsIncluded(normalized, options.Rules)) continue;

            overrides.Add(relative);
        }

        overrides.Sort(StringComparer.Ordinal);
        return overrides;
    }

    /// <summary>模组/资源包/光影目录由下载清单负责。</summary>
    private static bool IsContentPath(string relativePath)
    {
        var first = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return first is not null
               && ContentDirectories.Any(item => string.Equals(item.Directory, first, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<Dictionary<string, (string Sha1, string Sha512)>> HashFilesAsync(
        List<(string RelativePath, string FullPath)> files,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (sha1, sha512) = await FileHashing
                .Sha1AndSha512Async(file.FullPath, cancellationToken)
                .ConfigureAwait(false);

            result[file.RelativePath] = (sha1, sha512);

            done++;
            if (done % 10 == 0 || done == files.Count)
                progress?.Report($"正在计算文件哈希 {done}/{files.Count}…");
        }

        return result;
    }

    private async Task<Dictionary<string, MrpackFileEntry>> LookupAsync(
        List<(string RelativePath, string FullPath)> files,
        Dictionary<string, (string Sha1, string Sha512)> hashes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MrpackFileEntry>(StringComparer.OrdinalIgnoreCase);

        // Modrinth 的小写约定：查询用小写，匹配回来也用小写。
        // 一个哈希可能对应多个文件（同一个 jar 的启用/禁用副本内容完全相同），
        // 所以这里用"哈希 → 相对路径列表"，而不是一对一。
        var bySha1 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var sha1 = hashes[file.RelativePath].Sha1.ToLowerInvariant();
            if (!bySha1.TryGetValue(sha1, out var relatives))
                bySha1[sha1] = relatives = new List<string>();

            relatives.Add(file.RelativePath);
        }

        var versions = await _modrinth
            .GetVersionsByHashesAsync(bySha1.Keys.ToList(), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report($"Modrinth 识别到 {versions.Count}/{files.Count} 个文件");

        foreach (var (sha1, version) in versions)
        {
            if (!bySha1.TryGetValue(sha1, out var relatives)) continue;

            var versionFile = version.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.Sha1, sha1, StringComparison.OrdinalIgnoreCase));

            if (versionFile is null || string.IsNullOrWhiteSpace(versionFile.Url)) continue;

            foreach (var relative in relatives)
            {
                if (!hashes.TryGetValue(relative, out var hash)) continue;

                var (_, _, clientSide, serverSide) = ContentDirectories
                    .FirstOrDefault(item => relative.StartsWith(item.Directory + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));

                result[relative] = new MrpackFileEntry(
                    relative.Replace('\\', '/'),
                    hash.Sha1.ToLowerInvariant(),
                    hash.Sha512.ToLowerInvariant(),
                    versionFile.Size,
                    versionFile.Url,
                    clientSide,
                    serverSide);
            }
        }

        return result;
    }

    private static long WriteIndex(
        ZipArchive archive,
        string name,
        string packVersion,
        string summary,
        string minecraftVersion,
        string? loaderKey,
        string? loaderVersion,
        IReadOnlyList<MrpackFileEntry> entries)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft"] = minecraftVersion,
        };

        if (!string.IsNullOrWhiteSpace(loaderKey) && !string.IsNullOrWhiteSpace(loaderVersion))
            dependencies[loaderKey] = loaderVersion;

        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = packVersion,
            name,
            summary,
            files = entries.Select(entry => new
            {
                path = entry.Path,
                hashes = new { sha1 = entry.Sha1, sha512 = entry.Sha512 },
                env = new
                {
                    client = entry.ClientSide ? "required" : "unsupported",
                    server = entry.ServerSide ? "required" : "unsupported",
                },
                downloads = new[] { entry.DownloadUrl },
                fileSize = entry.Size,
            }),
            dependencies,
        };

        var zipEntry = archive.CreateEntry(IndexFileName, CompressionLevel.Optimal);
        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });

        using (var stream = zipEntry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }

        return Encoding.UTF8.GetByteCount(json);
    }
}
