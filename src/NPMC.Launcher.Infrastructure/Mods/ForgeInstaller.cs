using System.IO.Compression;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>准备阶段的产物：解析好的 profile + 已解出来的 universal jar。</summary>
public sealed record ForgeInstallPlan(
    ForgeInstallerProfile Profile,
    string InstallerPath,
    string? UniversalJarPath);

/// <summary>安装结果。</summary>
public sealed record ForgeInstallResult(
    string VersionId,
    int LibrariesDownloaded,
    int LibrariesSkipped,
    IReadOnlyList<string> Failures,
    string UniversalJarPath);

/// <summary>
/// 安装旧版 Forge（1.13 之前）。
///
/// 旧版安装包里带着 <c>install_profile.json</c>，其中：
///   - <c>versionInfo</c>：一份完整的版本 JSON（含 inheritsFrom 与 mainClass）
///   - <c>install.filePath</c>：内嵌的 universal jar（真正要放进 libraries 的那个）
///
/// 所以安装 = 解出 universal jar → 放到 libraries 下对应坐标 → 写版本 JSON → 下齐其余库。
/// **完全不需要运行 java**，也就不受"跑不了子进程"的环境限制。
///
/// 1.13+ 的安装包只有 processors（安装时要跑一堆 java 程序生成合并 jar），
/// 这条路这里不支持，会明确报出来。
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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(cacheDirectory);

        var installerPath = Path.Combine(cacheDirectory, $"forge-{minecraftVersion}-{forgeVersion}-installer.jar");

        // 两套命名都试一遍：老版本是 <mc>-<forge>-<mc>，新版本是 <mc>-<forge>
        Exception? failure = null;
        var downloaded = false;

        foreach (var url in ForgeCoordinates.InstallerUrlCandidates(minecraftVersion, forgeVersion))
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
        CancellationToken cancellationToken = default)
    {
        var profile = plan.Profile;

        if (!profile.CanInstallOffline)
            throw new NotSupportedException(
                $"{profile.MinecraftVersion} 的 Forge 安装包需要运行官方安装器（1.13+ 的 processors），暂不支持自动安装");

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
}
