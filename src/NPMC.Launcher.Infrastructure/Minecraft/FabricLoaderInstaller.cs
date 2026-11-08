using System.Net;
using System.Text.Json;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.Infrastructure.Minecraft;

/// <summary>Fabric 的一个加载器版本。</summary>
public sealed record FabricLoaderVersion(string Version, bool Stable)
{
    /// <summary>下拉里显示的文字：stable 标出来，避免选到预览版。</summary>
    public string DisplayText => Stable ? $"{Version}（stable）" : Version;

    /// <summary>Fabric 只有 stable / 非 stable 两档：非 stable 按 Beta 标记。</summary>
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
    /// <remarks>
    /// 两个必须处理的边界：
    ///   1. 早于 Fabric 的版本（1.0、c0.30_01c 等）直接返回 HTTP 400，视为"不支持"；
    ///   2. 没有 intermediary 映射的版本仍会返回完整列表，但每条的中介层版本是 0.0.0。
    ///      那是占位符而不是支持 —— 照这样列出来，用户会选中一个根本装不起来的组合。
    /// </remarks>
    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(
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
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // 400：Fabric 不认识这个游戏版本
            return Array.Empty<FabricLoaderVersion>();
        }

        return ParseLoaderVersions(json);
    }

    /// <summary>
    /// 解析 loader 列表。
    ///
    /// 有两类「intermediary 不写真实版本」的情况，不能混为一谈：
    ///   a. 部分条目是占位符 0.0.0（例如某些预览版还没发布映射）——此时列表里**有**真实映射的条目，
    ///      只保留那些，避免用户选中装不起来的组合；
    ///   b. 该游戏版本的条目**全部**是 0.0.0（26.x 就是这样：26.1.2 / 26.2 / 26.3 / 26.4-快照
    ///      的 255 条全是 0.0.0，但 /versions/loader/26.2/0.20.1/profile/json 正常返回）——
    ///      这说明这个接口对该版本族不提供映射信息，而不是"不支持"。
    ///      此时必须放行，否则 26.x 的 Fabric 会被整体判死（2026-11-08 实际踩到：
    ///      整合包 Fabulously Optimized 14.1.2 声明 26.2 + fabric-loader 0.20.1，
    ///      却报「Fabric 没有适用于 26.2 的加载器版本」）。
    ///
    /// 真正「这个组合装不装得上」由 <see cref="InstallAsync"/> 里请求 profile/json 来定夺。
    /// </summary>
    public static IReadOnlyList<FabricLoaderVersion> ParseLoaderVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        var mapped = new List<FabricLoaderVersion>();
        var all = new List<FabricLoaderVersion>();

        if (document.RootElement.ValueKind != JsonValueKind.Array) return mapped;

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

            all.Add(new FabricLoaderVersion(version!, stable));

            if (HasIntermediary(element, out _))
            {
                mapped.Add(new FabricLoaderVersion(version!, stable));
            }
        }

        // 一条真实映射都没有 = 该接口这个版本族不提供映射信息，按全量返回（见上面的注释 b）
        return mapped.Count > 0 ? mapped : all;
    }

    /// <summary>判断条目是否带真实的中介层映射（0.0.0 是占位符）。</summary>
    private static bool HasIntermediary(JsonElement element, out string version)
    {
        version = "";

        if (!element.TryGetProperty("intermediary", out var intermediary)) return false;
        if (!intermediary.TryGetProperty("version", out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) return false;

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text == "0.0.0") return false;

        version = text;
        return true;
    }


    /// <summary>
    /// 装之前的预检：这个 (游戏版本, 加载器版本) 组合到底装不装得上。
    /// 返回 null 表示可以装；否则返回一句可以直接展示给用户的原因。
    ///
    /// 存在的意义：整合包安装会先下载几十~上百 MB 的游戏本体，
    /// 装不上就应该在下载前说清楚，而不是下完才报错。
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
                ? $"Fabric 没有适用于 {gameVersion} 的加载器版本"
                : null;
        }

        var profileUrl = $"{LoaderMetadata}/{Uri.EscapeDataString(gameVersion)}/"
                         + $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        try
        {
            await _http.GetStringAsync(profileUrl, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (HttpRequestException ex)
        {
            return $"Fabric 加载器 {loaderVersion} 没有 {gameVersion} 的 profile"
                   + $"（HTTP {ex.StatusCode:D}），这个组合装不起来";
        }
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

        FabricLoaderVersion? chosen;
        if (loaderVersion is null)
        {
            if (versions.Count == 0)
                throw new InvalidOperationException($"Fabric 没有适用于 {gameVersion} 的加载器版本");

            chosen = versions.FirstOrDefault(version => version.Stable) ?? versions[0];
        }
        else
        {
            // 钉了版本就信它：列表里查不到（例如 26.x 的 intermediary 全是占位符，或整合包钉了一个
            // 比 meta 列表更新的构建）不等于装不上 —— 真正的判据是下面 profile/json 能否取到。
            chosen = versions.FirstOrDefault(version => version.Version == loaderVersion)
                     ?? new FabricLoaderVersion(loaderVersion, Stable: true);
        }

        var profileUrl = $"{LoaderMetadata}/{Uri.EscapeDataString(gameVersion)}/"
                         + $"{Uri.EscapeDataString(chosen.Version)}/profile/json";

        string profileJson;
        try
        {
            profileJson = await _http.GetStringAsync(profileUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Fabric 加载器 {chosen.Version} 没有 {gameVersion} 的 profile（HTTP {ex.StatusCode:D}），"
                + "这个组合装不起来：换一个加载器版本，或换一个游戏版本更低的整合包版本。",
                ex);
        }

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
