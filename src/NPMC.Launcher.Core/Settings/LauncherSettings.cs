using NPMC.Launcher.Core.Mirrors;

namespace NPMC.Launcher.Core.Settings;

/// <summary>
/// 启动器的全局设置。实例级设置（内存/窗口/Java 覆盖）在 <see cref="Instances.InstanceSettings"/> 里。
/// 这里只放"整个启动器共享"的选项。
/// </summary>
public sealed record LauncherSettings
{
    /// <summary>首选下载源。未指定 --source 时生效。</summary>
    public DownloadSource PreferredSource { get; init; } = DownloadSource.Official;

    /// <summary>镜像基地址，可替换成自建或其他镜像。</summary>
    public string BmclapiBaseUrl { get; init; } = "https://bmclapi2.bangbang93.com";

    /// <summary>首选源失败时是否回退到另一个源。建议保持 true —— 实测镜像节点约 6%~11% 会失败。</summary>
    public bool AllowSourceFallback { get; init; } = true;

    /// <summary>默认最大堆内存（MB）。实例没有单独设置时使用。</summary>
    public int DefaultMemoryMb { get; init; } = 4096;

    /// <summary>并发下载数（资源对象、运行时文件）。</summary>
    public int DownloadConcurrency { get; init; } = 12;

    public string Language { get; init; } = "zh-CN";

    /// <summary>转成镜像路由设置。</summary>
    public MirrorSettings ToMirrorSettings() => new()
    {
        Source = PreferredSource,
        BmclapiBaseUrl = BmclapiBaseUrl,
        AllowFallback = AllowSourceFallback,
    };
}
