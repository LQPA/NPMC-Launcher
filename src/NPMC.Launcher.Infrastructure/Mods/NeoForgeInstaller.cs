using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>
/// 安装 NeoForge。
///
/// 安装格式与 Forge 1.13+ 是同一套（install_profile 里是 processors + 包内 version.json），
/// 所以直接复用 <see cref="ForgeInstaller"/> 的"跑官方安装器"实现，只换下载地址：
///   https://maven.neoforged.net/releases/net/neoforged/neoforge/&lt;ver&gt;/neoforge-&lt;ver&gt;-installer.jar
/// </summary>
public sealed class NeoForgeInstaller
{
    private readonly ForgeInstaller _inner;

    public NeoForgeInstaller(HttpFileDownloader downloader) => _inner = new ForgeInstaller(downloader);

    public Task<ForgeInstallPlan> PrepareAsync(
        string minecraftVersion,
        string neoforgeVersion,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
        => _inner.PrepareAsync(
            minecraftVersion,
            neoforgeVersion,
            cacheDirectory,
            cancellationToken,
            NeoForgeVersions.InstallerUrlCandidates(neoforgeVersion));

    public Task<ForgeInstallResult> InstallAsync(
        LauncherPaths paths,
        ForgeInstallPlan plan,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? javaPath = null)
        => _inner.InstallAsync(paths, plan, progress, cancellationToken, javaPath);
}
