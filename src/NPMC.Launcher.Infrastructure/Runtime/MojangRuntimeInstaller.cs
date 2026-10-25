using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Runtime;

public sealed record RuntimeComponent(
    string Name,
    string Platform,
    string Version,
    string ManifestUrl,
    string ManifestSha1,
    long ManifestSize);

public sealed record RuntimeProgress(int Completed, int Total, long BytesDownloaded, string CurrentPath);

public sealed record RuntimeInstallResult(
    string JavaExePath,
    bool JavaExeExists,
    int TotalFiles,
    IReadOnlyList<string> Failures,
    long BytesDownloaded);

/// <summary>
/// 从 Mojang 官方运行时源安装 Java 到 &lt;启动器根&gt;\runtime\&lt;组件名&gt;\。
///
/// 实测基准（java-runtime-gamma / windows-x64 / 17.0.15）：
///   486 个条目 = 403 file + 83 directory + 0 link，解压后 86.9 MB。
///   单个运行时整包迁移（含空格与中文路径）后仍可正常运行。
///
/// 注意：bin\java.exe 只有 ~38KB，是 stub，真正的 JVM 在同目录 bin\server\jvm.dll，
/// 所以必须整包安装与整包迁移。legal\ 下的许可证文本属于 GPLv2+CE 分发要求，不要删。
/// </summary>
public sealed class MojangRuntimeInstaller
{
    public const string OfficialAllUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpFileDownloader _downloader;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;

    public MojangRuntimeInstaller(
        HttpFileDownloader downloader,
        Func<string, CancellationToken, Task<string>> fetch)
    {
        _downloader = downloader;
        _fetch = fetch;
    }

    public async Task<IReadOnlyList<RuntimeComponent>> GetComponentsAsync(
        string platform, string? allUrl = null, CancellationToken ct = default)
    {
        var json = await _fetch(allUrl ?? OfficialAllUrl, ct).ConfigureAwait(false);
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<ComponentDto>>>>(json, JsonOptions)
                  ?? throw new InvalidDataException("运行时清单解析失败。");

        if (!all.TryGetValue(platform, out var byComponent))
            return Array.Empty<RuntimeComponent>();

        var result = new List<RuntimeComponent>();
        foreach (var (name, entries) in byComponent)
        {
            if (entries.Count == 0 || entries[0].Manifest is null) continue;
            var entry = entries[0];
            result.Add(new RuntimeComponent(
                name,
                platform,
                entry.Version?.Name ?? "",
                entry.Manifest!.Url,
                entry.Manifest.Sha1,
                entry.Manifest.Size));
        }
        return result;
    }

    public async Task<RuntimeInstallResult> InstallAsync(
        string platform,
        string component,
        LauncherPaths paths,
        int concurrency = 12,
        IProgress<RuntimeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var components = await GetComponentsAsync(platform, ct: ct).ConfigureAwait(false);
        var target = components.FirstOrDefault(c => string.Equals(c.Name, component, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"平台 {platform} 上没有运行时组件 {component}。");

        var manifestJson = await _fetch(target.ManifestUrl, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<RuntimeManifestDto>(manifestJson, JsonOptions)
                       ?? throw new InvalidDataException("运行时 manifest 解析失败。");

        var root = paths.RuntimeDir(component);
        Directory.CreateDirectory(root);

        // 先建目录，再并发下载文件；link 类型在 windows 平台实测为 0，遇到就跳过。
        foreach (var (relative, file) in manifest.Files)
        {
            if (!string.Equals(file.Type, "directory", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Resolve(root, relative));
        }

        var files = manifest.Files
            .Where(kv => string.Equals(kv.Value.Type, "file", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var total = files.Count;
        var completed = new int[1];
        var bytes = new long[1];
        var failures = new ConcurrentBag<string>();

        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var tasks = files.Select(async pair =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var raw = pair.Value.Downloads?.Raw;
                if (raw is null || string.IsNullOrEmpty(raw.Url))
                {
                    Interlocked.Increment(ref completed[0]);
                    return;
                }

                var destination = Resolve(root, pair.Key);
                var downloaded = await _downloader
                    .EnsureAsync(raw.Url, destination, raw.Sha1, ct)
                    .ConfigureAwait(false);

                if (downloaded) Interlocked.Add(ref bytes[0], raw.Size);
                Interlocked.Increment(ref completed[0]);

                progress?.Report(new RuntimeProgress(
                    Volatile.Read(ref completed[0]), total, Interlocked.Read(ref bytes[0]), pair.Key));
            }
            catch (Exception ex)
            {
                failures.Add($"{pair.Key}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var javaExe = paths.JavaExe(component);
        return new RuntimeInstallResult(
            javaExe,
            File.Exists(javaExe),
            total,
            failures.ToList(),
            Interlocked.Read(ref bytes[0]));
    }

    /// <summary>把 manifest 里的 a/b/c 相对路径映射到本地路径。</summary>
    private static string Resolve(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- manifest DTO ----
    private sealed class ComponentDto
    {
        [JsonPropertyName("manifest")] public ManifestRefDto? Manifest { get; set; }
        [JsonPropertyName("version")] public ComponentVersionDto? Version { get; set; }
    }

    private sealed class ManifestRefDto
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed class ComponentVersionDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private sealed class RuntimeManifestDto
    {
        [JsonPropertyName("files")] public Dictionary<string, RuntimeFileDto> Files { get; set; } = new();
    }

    private sealed class RuntimeFileDto
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("executable")] public bool Executable { get; set; }
        [JsonPropertyName("downloads")] public RuntimeDownloadsDto? Downloads { get; set; }
    }

    private sealed class RuntimeDownloadsDto
    {
        [JsonPropertyName("raw")] public RawDownloadDto? Raw { get; set; }
        [JsonPropertyName("lzma")] public RawDownloadDto? Lzma { get; set; }
    }

    private sealed class RawDownloadDto
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
