using System.Net;
using System.Text.Json;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.Infrastructure.Minecraft;

/// <summary>Quilt 的一个加载器版本。</summary>
public sealed record QuiltLoaderVersion(string Version)
{
    /// <summary>Quilt 没有 stable 标记：版本号不带 '-' 视为正式版（0.29.2 / 0.30.1）。</summary>
    public bool Stable => !Version.Contains('-');

    public string DisplayText => Stable ? $"{Version}（stable）" : Version;

    public ReleaseChannel Channel => Stable ? ReleaseChannel.Release : ReleaseChannel.Beta;

    public string ChannelBadge => Channel.Badge();

    public string ChannelLabel => Channel.DisplayName();

    public string ChannelColor => Channel switch
    {
        ReleaseChannel.Alpha => "#A93226",
        ReleaseChannel.Beta => "#94611A",
        _ => "#2E7D46",
    };
}

/// <summary>
/// 安装 Quilt 加载器。
///
/// 和 Fabric 一样不需要跑安装器：meta.quiltmc.org 直接给一个
/// <c>profile/json</c>（标准版本 JSON + <c>inheritsFrom</c>），写盘 + 下载库即可。
///
/// 和 Fabric 的两个区别：
///   1. 版本列表是**全局的**（同一个列表对任何游戏版本都返回），能不能用某个游戏版本只能靠
///      profile 端点判断：不支持的游戏版本会返回 404（实测 1.7.10 就是 404）；
///   2. 没有 <c>stable</c> 字段，只能按版本号里有没有 '-' 区分正式版 / 预览版。
/// </summary>
public sealed class QuiltLoaderInstaller
{
    private const string LoaderMetadata = "https://meta.quiltmc.org/v3/versions/loader";

    private readonly HttpClient _http;

    public QuiltLoaderInstaller(HttpClient http) => _http = http;

    /// <summary>取该游戏版本可用的加载器版本（新→旧，正式版优先）。不支持时返回空。</summary>
    public async Task<IReadOnlyList<QuiltLoaderVersion>> GetLoaderVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        string json;

        try
        {
            json = await _http
                .GetStringAsync($"{LoaderMetadata}/{Uri.EscapeDataString(gameVersion)}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return Array.Empty<QuiltLoaderVersion>();
        }

        var versions = ParseLoaderVersions(json);
        if (versions.Count == 0) return versions;

        // 列表本身不区分游戏版本，只有 profile 端点能给"支不支持"的答案
        var probe = versions.FirstOrDefault(version => version.Stable) ?? versions[0];
        if (!await SupportsAsync(gameVersion, probe.Version, cancellationToken).ConfigureAwait(false))
            return Array.Empty<QuiltLoaderVersion>();

        return versions;
    }

    /// <summary>解析 loader 列表：去重、按版本号排序（新→旧，同版本正式版在前）。</summary>
    public static IReadOnlyList<QuiltLoaderVersion> ParseLoaderVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<QuiltLoaderVersion>();

        var versions = new List<string>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("loader", out var loader)) continue;

            var version = loader.TryGetProperty("version", out var versionElement)
                          && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(version)) versions.Add(version!);
        }

        return versions
            .Distinct(StringComparer.Ordinal)
            .Select(version => new QuiltLoaderVersion(version))
            .OrderByDescending(version => BaseVersion(version.Version))
            .ThenByDescending(version => version.Stable)
            .ThenByDescending(version => version.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把 "0.29.2" / "0.20.0-beta.9" 的数值部分变成可比较的 Version。</summary>
    private static Version BaseVersion(string version)
    {
        var dash = version.IndexOf('-');
        var basePart = dash >= 0 ? version[..dash] : version;

        var parts = basePart.Split('.');
        var numbers = new int[3];

        for (var i = 0; i < parts.Length && i < numbers.Length; i++)
            _ = int.TryParse(parts[i], out numbers[i]);

        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>
    /// 装之前的预检：这个 (游戏版本, 加载器版本) 组合有没有 profile。
    /// 返回 null 表示可以装；否则返回一句可以直接展示给用户的原因。
    /// 与 Fabric 的预检对称，用于在下载几十 MB 游戏本体之前就失败。
    /// </summary>
    public async Task<string?> TryCheckInstallableAsync(
        string gameVersion,
        string? loaderVersion,
        CancellationToken cancellationToken = default)
    {
        if (loaderVersion is null)
        {
            var versions = await GetLoaderVersionsAsync(gameVersion, cancellationToken).ConfigureAwait(false);
            return versions.Count == 0
                ? $"Quilt 没有适用于 {gameVersion} 的加载器版本"
                : null;
        }

        return await SupportsAsync(gameVersion, loaderVersion, cancellationToken).ConfigureAwait(false)
            ? null
            : $"Quilt 加载器 {loaderVersion} 没有 {gameVersion} 的 profile，这个组合装不起来";
    }
    private static string ProfileUrl(string gameVersion, string loaderVersion)
        => $"{LoaderMetadata}/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

    private async Task<bool> SupportsAsync(string gameVersion, string loaderVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(ProfileUrl(gameVersion, loaderVersion), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return false;
        }
    }

    /// <summary>安装加载器：写入 profile JSON 并下载它声明的库。</summary>
    /// <param name="loaderVersion">为空则取最新的正式版。</param>
    public async Task<FabricInstallResult> InstallAsync(
        LauncherPaths paths,
        string gameVersion,
        string? loaderVersion,
        HttpFileDownloader downloader,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetLoaderVersionsAsync(gameVersion, cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
            throw new InvalidOperationException($"Quilt 没有适用于 {gameVersion} 的加载器版本");

        var chosen = loaderVersion is null
            ? versions.FirstOrDefault(version => version.Stable) ?? versions[0]
            : versions.FirstOrDefault(version => version.Version == loaderVersion)
              ?? throw new InvalidOperationException($"Quilt 加载器 {loaderVersion} 不适用于 {gameVersion}");

        var profileJson = await _http
            .GetStringAsync(ProfileUrl(gameVersion, chosen.Version), cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(profileJson);
        var root = document.RootElement;

        var profileId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : $"quilt-loader-{chosen.Version}-{gameVersion}";

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
