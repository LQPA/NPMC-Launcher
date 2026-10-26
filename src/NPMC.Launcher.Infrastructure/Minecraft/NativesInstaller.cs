using System.IO.Compression;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Minecraft;

/// <summary>解压结果。</summary>
public sealed record NativesResult(int Extracted, IReadOnlyList<string> Failures, int LibrariesDownloaded);

/// <summary>
/// 解压旧式 natives。
///
/// 现代版本（1.19+）把 natives 当成普通库直接放进 classpath，不需要解压；
/// 但 1.13 之前的版本（如 1.7.10）声明的是
/// <c>"natives": { "windows": "natives-windows-${arch}" }</c>，
/// 必须把对应 classifier 的 jar 里的 dll 解到 <c>versions/&lt;id&gt;/natives</c>，
/// 否则启动时报 <c>UnsatisfiedLinkError: no lwjgl in java.library.path</c>。
/// </summary>
public sealed class NativesInstaller
{
    private readonly HttpFileDownloader _downloader;

    public NativesInstaller(HttpFileDownloader downloader) => _downloader = downloader;

    /// <summary>补齐某个版本的所有旧式 natives（已存在的文件不重复解）。</summary>
    public async Task<NativesResult> EnsureAsync(
        LauncherPaths paths,
        string versionId,
        VersionJson version,
        OsInfo os,
        CancellationToken cancellationToken = default)
    {
        var target = paths.NativesDir(versionId);
        Directory.CreateDirectory(target);

        var extracted = 0;
        var downloaded = 0;
        var failures = new List<string>();

        foreach (var library in version.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (library.Natives is null || library.Natives.Count == 0) continue;

            // 取当前系统的 classifier，并展开 ${arch}
            if (!library.Natives.TryGetValue(os.Name, out var classifierTemplate) || classifierTemplate is null)
                continue;

            var classifier = ExpandArch(classifierTemplate, os);

            if (library.Downloads?.Classifiers is null
                || !library.Downloads.Classifiers.TryGetValue(classifier, out var artifact)
                || string.IsNullOrWhiteSpace(artifact.Path))
            {
                continue;
            }

            var jarPath = paths.LibraryFile(artifact.Path);

            try
            {
                var outcome = await _downloader
                    .EnsureFromAsync(
                        string.IsNullOrWhiteSpace(artifact.Url) ? Array.Empty<string>() : new[] { artifact.Url },
                        jarPath,
                        string.IsNullOrWhiteSpace(artifact.Sha1) ? null : artifact.Sha1,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Downloaded) downloaded++;

                extracted += Extract(jarPath, target);
            }
            catch (Exception ex)
            {
                failures.Add($"{library.Name}（{classifier}）：{ex.GetBaseException().Message}");
            }
        }

        return new NativesResult(extracted, failures, downloaded);
    }

    /// <summary>把 jar 里的动态库解到目标目录（扁平化，跳过 META-INF）。</summary>
    private static int Extract(string jarPath, string targetDirectory)
    {
        if (!File.Exists(jarPath)) return 0;

        var count = 0;

        using var archive = ZipFile.OpenRead(jarPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;                    // 目录条目
            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsNativeLibrary(entry.Name)) continue;

            // 官方启动器也是按文件名扁平化提取
            var destination = Path.Combine(targetDirectory, entry.Name);

            try
            {
                entry.ExtractToFile(destination, overwrite: true);
                count++;
            }
            catch (IOException)
            {
                // 文件被占用（游戏正在运行）时不算失败
            }
        }

        return count;
    }

    private static bool IsNativeLibrary(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jnilib", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>1.7.10 的 classifier 里带 ${arch}，按进程位数展开。</summary>
    public static string ExpandArch(string classifier, OsInfo os)
    {
        if (!classifier.Contains("${arch}", StringComparison.Ordinal)) return classifier;

        var arch = os.Arch.Contains("64", StringComparison.Ordinal) || Environment.Is64BitProcess
            ? "64"
            : "32";

        return classifier.Replace("${arch}", arch, StringComparison.Ordinal);
    }
}
