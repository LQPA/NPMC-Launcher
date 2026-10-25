using System.Text.Json;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.Infrastructure.Minecraft;

/// <summary>Fabric 的一个加载器版本。</summary>
public sealed record FabricLoaderVersion(string Version, bool Stable)
{
    /// <summary>下拉里显示的文字：stable 标出来，避免选到预览版。</summary>
    public string DisplayText => Stable ? $"{Version}（stable）" : Version;
}

/// <summary>安装结果。</summary>
public sealed record FabricInstallResult(
    string ProfileId,
    string LoaderVersion,
    int LibrariesDownloaded,
    int LibrariesSkipped);

/// <summary>
/// 安装 Fabric 加载器。
///
/// 为什么 Fabric 最简单：它**不需要运行任何安装器**。
/// meta.fabricmc.net 直接给出一个 <c>profile/json</c>，内容是一个标准版本 JSON，
/// 只是带 <c>inheritsFrom: "1.20.1"</c> —— 启动时把父版本合并进来即可。
/// 所以"安装加载器" = 写一个 JSON 文件 + 下载它列出的库。
///
/// 对比：Forge / NeoForge 只提供 installer jar，必须用 java 执行它，
/// 会改动版本目录、还要处理它自己的下载步骤，复杂度高一个量级。
///
/// 注意 profile 里的库只有 **Maven 坐标**（没有 path 字段），
/// 路径要靠 <see cref="MavenPath"/> 自己推。
/// </summary>
public sealed class FabricLoaderInstaller
{
    private const string LoaderMetadata = "https://meta.fabricmc.net/v2/versions/loader";

    private readonly HttpClient _http;

    public FabricLoaderInstaller(HttpClient http) => _http = http;

    /// <summary>取该游戏版本可用的加载器版本（新→旧）。</summary>
    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        var json = await _http
            .GetStringAsync($"{LoaderMetadata}/{Uri.EscapeDataString(gameVersion)}", cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var versions = new List<FabricLoaderVersion>();

        if (document.RootElement.ValueKind != JsonValueKind.Array) return versions;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("loader", out var loader)) continue;

            var version = loader.TryGetProperty("version", out var versionElement)
                          && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(version)) continue;

            var stable = loader.TryGetProperty("stable", out var stableElement)
                         && stableElement.ValueKind == JsonValueKind.True;

            versions.Add(new FabricLoaderVersion(version!, stable));
        }

        return versions;
    }

    /// <summary>
    /// 安装加载器：写入 profile JSON 并下载它声明的库。
    /// </summary>
    /// <param name="loaderVersion">为空则取最新的 stable 版本。</param>
    public async Task<FabricInstallResult> InstallAsync(
        LauncherPaths paths,
        string gameVersion,
        string? loaderVersion,
        HttpFileDownloader downloader,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetLoaderVersionsAsync(gameVersion, cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
            throw new InvalidOperationException($"Fabric 没有适用于 {gameVersion} 的加载器版本");

        var chosen = loaderVersion is null
            ? versions.FirstOrDefault(version => version.Stable) ?? versions[0]
            : versions.FirstOrDefault(version => version.Version == loaderVersion)
              ?? throw new InvalidOperationException($"Fabric 加载器 {loaderVersion} 不适用于 {gameVersion}");

        var profileUrl = $"{LoaderMetadata}/{Uri.EscapeDataString(gameVersion)}/"
                         + $"{Uri.EscapeDataString(chosen.Version)}/profile/json";

        var profileJson = await _http.GetStringAsync(profileUrl, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(profileJson);
        var root = document.RootElement;

        var profileId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : $"fabric-loader-{chosen.Version}-{gameVersion}";

        // 版本 JSON 原样落盘：启动时按 inheritsFrom 合并父版本
        Directory.CreateDirectory(paths.VersionDir(profileId));
        await AtomicFile
            .WriteAllTextAsync(paths.VersionJson(profileId), profileJson, cancellationToken)
            .ConfigureAwait(false);

        var downloaded = 0;
        var skipped = 0;

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                var coordinate = library.TryGetProperty("name", out var nameElement)
                                 && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                var repository = library.TryGetProperty("url", out var urlElement)
                                 && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;

                // 没有仓库地址的条目（例如由父版本提供的库）跳过
                if (string.IsNullOrWhiteSpace(coordinate) || string.IsNullOrWhiteSpace(repository)) continue;

                var sha1 = library.TryGetProperty("sha1", out var shaElement)
                           && shaElement.ValueKind == JsonValueKind.String
                    ? shaElement.GetString()
                    : null;

                var mavenPath = MavenPath.FromCoordinate(coordinate!);
                var url = repository!.TrimEnd('/') + "/" + mavenPath;

                var outcome = await downloader
                    .EnsureFromAsync(
                        new[] { url },
                        paths.LibraryFile(mavenPath),
                        string.IsNullOrWhiteSpace(sha1) ? null : sha1)
                    .ConfigureAwait(false);

                if (outcome.Downloaded) downloaded++;
                else skipped++;
            }
        }

        return new FabricInstallResult(profileId, chosen.Version, downloaded, skipped);
    }
}
