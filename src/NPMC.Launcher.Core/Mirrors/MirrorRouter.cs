namespace NPMC.Launcher.Core.Mirrors;

/// <summary>下载源。</summary>
public enum DownloadSource
{
    Official = 0,
    Bmclapi = 1,
}

/// <summary>
/// 资源类别。
///
/// 为什么必须由调用方显式指定，而不是从 URL 推断：
///   ClientJar 与 LoggingConfig 都在 piston-data.mojang.com/v1/objects/ 下，
///   但前者可以路由到镜像（虽然实测最终回源官方），
///   后者在 BMCLAPI 上返回 404，必须直连官方。
/// </summary>
public enum ResourceKind
{
    VersionManifest,
    VersionMetadata,
    ClientJar,
    Library,
    AssetIndex,
    AssetObject,
    LoggingConfig,
}

public sealed record MirrorSettings
{
    /// <summary>首选下载源。</summary>
    public DownloadSource Source { get; init; } = DownloadSource.Official;

    /// <summary>BMCLAPI 基地址（可自行替换为其它镜像）。</summary>
    public string BmclapiBaseUrl { get; init; } = "https://bmclapi2.bangbang93.com";

    /// <summary>首选源失败时是否回退到另一个源。</summary>
    public bool AllowFallback { get; init; } = true;
}

/// <summary>一条资源在某个源下的候选地址。</summary>
public sealed record MirrorTarget(DownloadSource Source, string Url, bool IsMirrored);

/// <summary>
/// 把 Mojang 官方 URL 重写为镜像 URL。纯字符串变换，不发请求，便于离线单测。
///
/// 重写规则来自实测（2026-10，BMCLAPI）：
///   launchermeta.mojang.com/mc/game/...         → /mc/game/...            （路径形状一致）
///   piston-meta.mojang.com/v1/packages/&lt;sha&gt;/ → /v1/packages/&lt;sha&gt;/   （路径形状一致）
///   piston-data.mojang.com/v1/objects/&lt;sha&gt;/  → /v1/objects/&lt;sha&gt;/    （路径形状一致）
///   libraries.minecraft.net/&lt;maven path&gt;      → /maven/&lt;maven path&gt;
///   resources.download.minecraft.net/&lt;2&gt;/&lt;h&gt;   → /assets/&lt;2&gt;/&lt;h&gt;
///
/// 另外两点是实测踩出来的：
///   1. LoggingConfig 在 BMCLAPI 上 404 —— 该类别永不路由到镜像。
///   2. 镜像会 302 到带 ?s=&amp;e= 的签名地址，因此最终的下载地址不可持久化；
///      本类只负责"入口地址"，跟随重定向由下载器完成。
/// </summary>
public sealed class MirrorRouter
{
    private readonly MirrorSettings _settings;
    private readonly string _mirrorBase;

    public MirrorRouter(MirrorSettings? settings = null)
    {
        _settings = settings ?? new MirrorSettings();
        _mirrorBase = (_settings.BmclapiBaseUrl ?? string.Empty).TrimEnd('/');
    }

    public MirrorSettings Settings => _settings;

    /// <summary>按优先级返回候选地址，第一个是首选。</summary>
    public IReadOnlyList<MirrorTarget> Resolve(string officialUrl, ResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(officialUrl))
            return Array.Empty<MirrorTarget>();

        var mirrorUrl = TryBuildMirrorUrl(officialUrl, kind);
        var targets = new List<MirrorTarget>(2);

        if (_settings.Source == DownloadSource.Bmclapi)
        {
            if (mirrorUrl is not null)
                targets.Add(new MirrorTarget(DownloadSource.Bmclapi, mirrorUrl, true));

            // 镜像不可用，或允许回退时补上官方源
            if (mirrorUrl is null || _settings.AllowFallback)
                targets.Add(new MirrorTarget(DownloadSource.Official, officialUrl, false));
        }
        else
        {
            targets.Add(new MirrorTarget(DownloadSource.Official, officialUrl, false));

            if (mirrorUrl is not null && _settings.AllowFallback)
                targets.Add(new MirrorTarget(DownloadSource.Bmclapi, mirrorUrl, true));
        }

        return targets;
    }

    /// <summary>只取首选地址；无法解析时返回 null。</summary>
    public string? ResolvePrimary(string officialUrl, ResourceKind kind)
    {
        var targets = Resolve(officialUrl, kind);
        return targets.Count > 0 ? targets[0].Url : null;
    }

    /// <summary>该类别能否镜像。LoggingConfig 恒为 false。</summary>
    public static bool IsMirrorable(ResourceKind kind) => kind != ResourceKind.LoggingConfig;

    /// <summary>构造镜像 URL；不可镜像或主机未知时返回 null。</summary>
    public string? TryBuildMirrorUrl(string officialUrl, ResourceKind kind)
    {
        if (!IsMirrorable(kind)) return null;
        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var path = uri.AbsolutePath;
        var mirrorPath = uri.Host.ToLowerInvariant() switch
        {
            // 元数据与文件对象：路径形状与官方一致
            "launchermeta.mojang.com" => path,
            "piston-meta.mojang.com" => path,
            "piston-data.mojang.com" => path,

            // 库：官方根即 maven 根，镜像挂在 /maven 下
            "libraries.minecraft.net" => "/maven" + path,

            // 资源对象：镜像挂在 /assets 下
            "resources.download.minecraft.net" => "/assets" + path,

            _ => null,
        };

        if (mirrorPath is null || string.IsNullOrEmpty(_mirrorBase)) return null;

        return _mirrorBase + mirrorPath + uri.Query;
    }
}
