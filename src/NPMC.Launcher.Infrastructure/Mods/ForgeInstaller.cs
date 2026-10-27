using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Launch;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>准备阶段的产物：解析好的 profile + 已解出来的 universal jar。</summary>
public sealed record ForgeInstallPlan(
    ForgeInstallerProfile Profile,
    string InstallerPath,
    string? UniversalJarPath);

/// <summary>安装结果。<see cref="UsedOfficialInstaller"/> 表示 1.13+ 走了官方安装器。</summary>
public sealed record ForgeInstallResult(
    string VersionId,
    int LibrariesDownloaded,
    int LibrariesSkipped,
    IReadOnlyList<string> Failures,
    string UniversalJarPath,
    bool UsedOfficialInstaller = false);

/// <summary>
/// 安装 Forge。两条路：
///
/// 1. **1.12 及以前**：读 <c>install_profile.json</c> 里的 <c>versionInfo</c> 与内嵌 universal jar，
///    解包 → 放库 → 写版本 JSON。**不需要运行 java**。
/// 2. **1.13 及以后**：安装包里只有 processors（安装时要跑一堆 java 程序把 client jar 打补丁、
///    合并 mapping），本启动器不自己实现这套工具链，而是把**官方安装器**当黑盒跑：
///       java -jar forge-&lt;mc&gt;-&lt;ver&gt;-installer.jar --installClient &lt;根目录&gt;
///    它会自己下库、跑 processors，并把版本 JSON 写进 versions/&lt;版本&gt;/。
///    这也是 PCL2 / HMCL 在"跑官方安装器"这条路上的做法；代价是安装阶段需要 Java，
///    而且它用自己的官方源下载（不走我们的镜像设置）。
///
/// 运行安装器前必须已经有游戏本体（versions/&lt;mc&gt;/ 里的 json + client jar），
/// 否则 processors 找不到输入 jar。
/// </summary>
public sealed class ForgeInstaller
{
    private readonly HttpFileDownloader _downloader;

    public ForgeInstaller(HttpFileDownloader downloader) => _downloader = downloader;

    /// <summary>下载安装包并解析 profile（不做任何安装动作）。</summary>
    public async Task<ForgeInstallPlan> PrepareAsync(
        string minecraftVersion,
        string forgeVersion,
        string cacheDirectory,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? installerUrls = null)
    {
        Directory.CreateDirectory(cacheDirectory);

        var candidates = installerUrls ?? ForgeCoordinates.InstallerUrlCandidates(minecraftVersion, forgeVersion);

        // 文件名跟具体 loader 走（Forge / NeoForge 的安装包都叫 installer.jar，混用会互相覆盖）
        var fileName = installerUrls is null
            ? $"forge-{minecraftVersion}-{forgeVersion}-installer.jar"
            : Path.GetFileName(new Uri(candidates[0]).AbsolutePath);

        var installerPath = Path.Combine(cacheDirectory, fileName);

        // 两套命名都试一遍：老版本是 <mc>-<forge>-<mc>，新版本是 <mc>-<forge>
        Exception? failure = null;
        var downloaded = false;

        foreach (var url in candidates)
        {
            try
            {
                await _downloader.EnsureFromAsync(new[] { url }, installerPath, null, cancellationToken)
                    .ConfigureAwait(false);

                downloaded = true;
                break;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (!downloaded)
            throw new InvalidOperationException(
                $"下载 Forge 安装包失败（{minecraftVersion}-{forgeVersion}）：{failure?.GetBaseException().Message}", failure);

        string profileJson;

        using (var archive = ZipFile.OpenRead(installerPath))
        {
            var entry = archive.GetEntry("install_profile.json")
                        ?? throw new InvalidDataException("Forge 安装包里没有 install_profile.json");

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);

            profileJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var profile = ForgeCoordinates.Parse(profileJson, $"{minecraftVersion}-forge{forgeVersion}");

        // 解出 universal jar（旧版才有；新版没有这个文件）
        string? universalJar = null;

        if (profile.UniversalJarEntry is { Length: > 0 } entryName)
        {
            using var archive = ZipFile.OpenRead(installerPath);
            var entry = archive.GetEntry(entryName);

            if (entry is not null)
            {
                universalJar = Path.Combine(cacheDirectory, Path.GetFileName(entryName));
                if (File.Exists(universalJar)) File.Delete(universalJar);

                entry.ExtractToFile(universalJar);
            }
        }

        return new ForgeInstallPlan(profile, installerPath, universalJar);
    }

    /// <summary>把 profile 写入版本目录，并放好 universal jar 与其余库。</summary>
    public async Task<ForgeInstallResult> InstallAsync(
        LauncherPaths paths,
        ForgeInstallPlan plan,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? javaPath = null)
    {
        var profile = plan.Profile;

        // 1.13+：交给官方安装器（需要 Java）
        if (!profile.CanInstallOffline)
            return await InstallWithOfficialInstallerAsync(paths, plan, javaPath, progress, cancellationToken)
                .ConfigureAwait(false);

        // 1) 版本 JSON
        Directory.CreateDirectory(paths.VersionDir(profile.VersionId));
        await File.WriteAllTextAsync(paths.VersionJson(profile.VersionId), profile.RawVersionJson, cancellationToken)
            .ConfigureAwait(false);

        // 2) universal jar 放到 libraries 下它声明的坐标位置
        var forgeLibrary = profile.Version.Libraries.FirstOrDefault(library =>
            library.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase));

        var universalTarget = "";

        if (forgeLibrary is not null && plan.UniversalJarPath is not null)
        {
            universalTarget = paths.LibraryFile(ForgeCoordinates.LibraryPath(forgeLibrary.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(universalTarget)!);
            File.Copy(plan.UniversalJarPath, universalTarget, overwrite: true);
        }

        // 3) 其余库（旧版 Forge 的库只给 maven 根地址，不给完整下载地址）
        var downloaded = 0;
        var skipped = 0;
        var failures = new List<string>();
        var libraries = profile.Version.Libraries;

        for (var index = 0; index < libraries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var library = libraries[index];

            if (string.IsNullOrWhiteSpace(library.Name)) continue;
            if (forgeLibrary is not null && library.Name == forgeLibrary.Name) continue;
            if (library.Name.Count(character => character == ':') < 2) continue;

            var relative = ForgeCoordinates.LibraryPath(library.Name);
            var destination = paths.LibraryFile(relative);

            progress?.Report($"库 {index + 1}/{libraries.Count}  {relative}");

            var baseUrl = string.IsNullOrWhiteSpace(library.Url)
                ? ForgeCoordinates.MavenBase
                : library.Url!;

            var candidates = new[]
            {
                baseUrl.TrimEnd('/') + "/" + relative,
                "https://libraries.minecraft.net/" + relative,
                "https://maven.minecraftforge.net/" + relative,
                "https://repo1.maven.org/maven2/" + relative,
            };

            try
            {
                var outcome = await _downloader
                    .EnsureFromAsync(candidates, destination, null, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Downloaded) downloaded++;
                else skipped++;
            }
            catch (Exception ex)
            {
                failures.Add($"{library.Name}：{ex.GetBaseException().Message}");
            }
        }

        return new ForgeInstallResult(profile.VersionId, downloaded, skipped, failures, universalTarget);
    }

    /// <summary>
    /// 1.13+：跑官方安装器（installer.jar --installClient &lt;根目录&gt;）。
    ///
    /// 为什么不自已实现 processors：那套工具链要下载 installertools / mcp 数据、
    /// 跑 binarypatcher 给 client.jar 打补丁、合并 mapping，每一步都依赖 Forge 自己的
    /// java 程序；官方安装器本身就是这套流程的参考实现，当黑盒跑最省事也最不容易跑偏。
    /// </summary>
    private async Task<ForgeInstallResult> InstallWithOfficialInstallerAsync(
        LauncherPaths paths,
        ForgeInstallPlan plan,
        string? javaPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var profile = plan.Profile;
        var minecraftVersion = profile.MinecraftVersion;

        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new InvalidOperationException("这个 Forge 安装包没有声明对应的游戏版本，无法准备安装环境");

        // processors 需要原版 client jar 当输入
        var vanillaJson = paths.VersionJson(minecraftVersion);
        var vanillaJar = paths.ClientJar(minecraftVersion);

        if (!File.Exists(vanillaJson) || !File.Exists(vanillaJar))
        {
            throw new InvalidOperationException(
                $"安装 Forge 前需要先装好游戏本体 {minecraftVersion}"
                + $"（缺少 {vanillaJson} 或 {vanillaJar}）");
        }

        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            throw new InvalidOperationException(
                $"安装 {minecraftVersion} 的 Forge 需要 Java，但没找到可用的 java.exe。"
                + "可以先在设置页预下载对应 Java 运行时");
        }

        // Forge 安装器会往 launcher_profiles.json 里注入 profile；文件不存在时它会直接失败
        EnsureLauncherProfiles(paths.SharedRoot);

        // 安装过程要写库和版本目录；Low 完整性下的 java 处理文件会出问题（见 IntegrityGuard）
        IntegrityGuard.EnsureExecutableRunsAtLauncherLevel(javaPath);

        // 官方安装器只会从 maven.neoforged.net 逐一下载；该域名在部分网络下会超时。
        // 先按 installer 自带的 version.json 从 BMCLAPI 补齐缺失库，安装器随后会直接跳过网络。
        await PrefetchInstallerLibrariesAsync(paths, plan.InstallerPath, progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report($"运行 Forge 官方安装器（{Path.GetFileName(plan.InstallerPath)}）…");

        var startInfo = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = paths.SharedRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(plan.InstallerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(paths.SharedRoot);

        var output = new List<string>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("启动 Forge 官方安装器失败");

            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;

                lock (output)
                {
                    output.Add(line);
                    if (output.Count > 2000) output.RemoveAt(0);
                }

                // 安装器的输出很长（逐条打补丁），只把每行原样丢给进度回调，界面自己截断
                progress?.Report(line.Trim());
            }

            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode == 0) break;

            if (attempt == 1)
            {
                string tail;
                lock (output) tail = string.Join(" / ", output.TakeLast(4));
                throw new InvalidOperationException(
                    $"Forge 官方安装器退出码 {process.ExitCode}：{tail}");
            }

            // 官方 maven.neoforged.net 在部分网络下会超时；用 BMCLAPI 镜像预下载失败清单，再跑一次
            var missing = ParseMissingLibraries(output);
            if (missing.Count == 0)
            {
                string tail;
                lock (output) tail = string.Join(" / ", output.TakeLast(4));
                throw new InvalidOperationException(
                    $"Forge 官方安装器退出码 {process.ExitCode}：{tail}");
            }

            progress?.Report($"官方源下载失败，改用 BMCLAPI 预下载 {missing.Count} 个库…");
            var mirrored = await PrefetchMirrorLibrariesAsync(
                paths, missing, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report($"BMCLAPI 预下载完成：{mirrored}/{missing.Count}");

            lock (output) output.Clear();
        }

        // 安装器写出的版本 id：profile 里有就用它，没有就在 versions/ 里找 <mc>-forge*
        var versionId = profile.VersionId;

        if (string.IsNullOrWhiteSpace(versionId) || !File.Exists(paths.VersionJson(versionId)))
            versionId = FindInstalledForgeVersionId(paths, minecraftVersion) ?? versionId;

        if (!File.Exists(paths.VersionJson(versionId)))
            throw new InvalidOperationException(
                $"安装器运行成功，但没有找到生成的版本 JSON（预期 {paths.VersionJson(versionId)}）");

        progress?.Report($"Forge 已安装：{versionId}");

        return new ForgeInstallResult(
            versionId,
            LibrariesDownloaded: 0,
            LibrariesSkipped: 0,
            Failures: Array.Empty<string>(),
            UniversalJarPath: "",
            UsedOfficialInstaller: true);
    }

    /// <summary>安装器不一定按 profile.VersionId 命名目录；按常见模式兜底找最新的一个。</summary>
    private static string? FindInstalledForgeVersionId(LauncherPaths paths, string minecraftVersion)
    {
        try
        {
            if (!Directory.Exists(paths.Versions)) return null;

            foreach (var pattern in new[] { $"{minecraftVersion}-forge*", "neoforge-*" })
            {
                var found = Directory.EnumerateDirectories(paths.Versions, pattern)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && File.Exists(paths.VersionJson(name)))
                    .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (found is not null) return found;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private const string BamclapiMaven = "https://bmclapi2.bangbang93.com/maven/";

    /// <summary>从 installer 的 version.json 读取库清单，用 BMCLAPI 补齐缺失文件。</summary>
    private async Task PrefetchInstallerLibrariesAsync(
        LauncherPaths paths,
        string installerPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(installerPath);
            var candidates = new List<(string Path, string Sha1)>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // version.json 是运行时库；install_profile.json 里还有处理器库（asm-analysis 等），两边都要补
            foreach (var entryName in new[] { "version.json", "install_profile.json" })
            {
                var entry = archive.GetEntry(entryName);
                if (entry is null) continue;

                using var stream = entry.Open();
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                    || libraries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var library in libraries.EnumerateArray())
                {
                    if (!library.TryGetProperty("downloads", out var downloads)
                        || !downloads.TryGetProperty("artifact", out var artifact))
                        continue;

                    var path = artifact.TryGetProperty("path", out var pathElement)
                        ? pathElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var sha1 = artifact.TryGetProperty("sha1", out var shaElement)
                        ? shaElement.GetString()
                        : null;

                    var destination = paths.LibraryFile(path!);
                    if (File.Exists(destination)) continue;
                    if (!seenPaths.Add(path!)) continue;

                    candidates.Add((path!, sha1 ?? ""));
                }
            }

            if (candidates.Count == 0) return;

            progress?.Report($"从 BMCLAPI 预补 {candidates.Count} 个安装器库…");

            var gate = new SemaphoreSlim(8);
            var downloaded = 0;

            var tasks = candidates.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await _downloader.EnsureFromAsync(
                        new[] { BamclapiMaven + item.Path },
                        paths.LibraryFile(item.Path),
                        string.IsNullOrWhiteSpace(item.Sha1) ? null : item.Sha1,
                        cancellationToken).ConfigureAwait(false);

                    if (result.Downloaded) Interlocked.Increment(ref downloaded);
                }
                catch (Exception ex)
                {
                    progress?.Report($"预补失败 {item.Path}：{ex.GetBaseException().Message}");
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            progress?.Report($"BMCLAPI 预补完成：{downloaded}/{candidates.Count}");
        }
        catch (Exception ex)
        {
            progress?.Report($"预补安装器库跳过：{ex.GetBaseException().Message}");
        }
    }

    private static IReadOnlyList<string> ParseMissingLibraries(List<string> output)
    {
        lock (output)
        {
            var start = output.FindIndex(line =>
                line.Contains("These libraries failed to download", StringComparison.OrdinalIgnoreCase));

            if (start < 0) return Array.Empty<string>();

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = start + 1; i < output.Count; i++)
            {
                var line = output[i].Trim();

                if (line.StartsWith("There was an error", StringComparison.OrdinalIgnoreCase)) break;
                if (line.Length == 0 || line.Contains(' ') || line.Count(c => c == ':') < 2) continue;
                if (seen.Add(line)) result.Add(line);
            }

            return result;
        }
    }

    /// <summary>官方 maven 超时时，从 BMCLAPI 镜像把安装器清单里的库补到 libraries。</summary>
    private async Task<int> PrefetchMirrorLibrariesAsync(
        LauncherPaths paths,
        IReadOnlyList<string> coordinates,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(8);
        var downloaded = 0;

        var tasks = coordinates.Select(async coordinate =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var relative = MavenPath.FromCoordinate(coordinate);
                var url = BamclapiMaven + relative;
                var destination = paths.LibraryFile(relative);

                // 失败清单里的文件即使存在也不可信（可能是上次中断的空文件）
                if (File.Exists(destination)) File.Delete(destination);

                var result = await _downloader
                    .EnsureFromAsync(new[] { url }, destination, null, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Downloaded)
                {
                    Interlocked.Increment(ref downloaded);
                    progress?.Report($"镜像补齐 {coordinate}");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"镜像补齐失败 {coordinate}：{ex.GetBaseException().Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return downloaded;
    }

    private static void EnsureLauncherProfiles(string root)
    {
        var path = Path.Combine(root, "launcher_profiles.json");
        if (File.Exists(path)) return;

        File.WriteAllText(
            path,
            "{\n  \"profiles\": {},\n  \"settings\": {},\n  \"version\": 2\n}",
            new UTF8Encoding(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已经退出就没什么可做的
        }
    }

    /// <summary>
    /// 给"跑官方安装器"挑一个 Java：优先该版本声明的运行时组件（&lt;root&gt;\runtime\&lt;组件&gt;），
    /// 其次主版本相同的系统 Java，最后才退到更新的版本。
    /// 老版 Forge 安装器是按 Java 8 编译的，用 17 跑 1.16 的安装器有概率失败 —— 所以"同主版本"优先。
    /// </summary>
    public static string? ResolveInstallerJava(LauncherPaths paths, VersionJson? vanillaVersion)
    {
        var requiredMajor = vanillaVersion?.JavaVersion?.MajorVersion ?? 8;
        var component = vanillaVersion?.JavaVersion?.Component;

        if (!string.IsNullOrWhiteSpace(component))
        {
            var bundled = paths.JavaExe(component!);
            if (File.Exists(bundled)) return bundled;
        }

        var installs = JavaLocator.Discover(paths);

        var exact = installs.FirstOrDefault(item => item.MajorVersion == requiredMajor);
        if (exact is not null) return exact.Path;

        return installs
            .Where(item => item.MajorVersion > requiredMajor)
            .OrderBy(item => item.MajorVersion)
            .FirstOrDefault()?.Path;
    }
}
