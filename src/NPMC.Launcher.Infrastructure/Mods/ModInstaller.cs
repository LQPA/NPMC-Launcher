using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Modrinth;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>计划里要装的一个文件。</summary>
public sealed record InstallTarget(
    string ProjectId,
    string ProjectTitle,
    string VersionLabel,
    string FileName,
    string DownloadUrl,
    string? Sha1,
    bool IsDependency)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ProjectTitle) ? ProjectId : ProjectTitle;

    /// <summary>该依赖是作者钉死的版本（而不是我们挑的最新版）。</summary>
    public bool Pinned { get; init; }
}

/// <summary>解析出来的安装计划：主体 + 它要求的依赖。</summary>
public sealed record InstallPlan(IReadOnlyList<InstallTarget> Targets)
{
    public IEnumerable<InstallTarget> Dependencies => Targets.Where(target => target.IsDependency);
    public IEnumerable<InstallTarget> Main => Targets.Where(target => !target.IsDependency);
}

/// <summary>安装结果。</summary>
public sealed record InstallOutcome(
    IReadOnlyList<InstallTarget> Installed,
    IReadOnlyList<InstallTarget> Skipped,
    IReadOnlyList<(InstallTarget Target, string Error)> Failed);

/// <summary>
/// 从 Modrinth 安装模组，含**必需依赖**的递归解析。
///
/// 几个刻意的取舍：
///   - **只自动装 required**。optional 装了会改变游戏行为（用户的整合包不是我们决定的），
///     incompatible 则绝不能装。这两类只在计划里说明，不执行。
///   - **同名文件已存在就跳过**，不覆盖 —— 用户可能手动换过版本。
///   - 依赖可能只钉了项目、也可能钉死版本；钉死时直接用，否则在该项目的版本里
///     按游戏版本 + 加载器挑最新的正式版。
/// </summary>
public sealed class ModInstaller
{
    private const int MaxDependencyDepth = 12;   // 防环 + 防病态依赖链

    private readonly ModrinthClient _modrinth;
    private readonly HttpFileDownloader _downloader;

    public ModInstaller(ModrinthClient modrinth, HttpFileDownloader downloader)
    {
        _modrinth = modrinth;
        _downloader = downloader;
    }

    /// <summary>用已有的 HttpClient 构造（与界面/命令行的其他网络调用共用连接池）。</summary>
    public static ModInstaller Create(HttpClient http) =>
        new(new ModrinthClient(http), new HttpFileDownloader(http));

    /// <summary>解析安装计划：主体版本 + 递归的必需依赖。</summary>
    /// <param name="ignorePinnedDependencies">
    /// 忽略作者钉死的依赖版本，改为"该项目当前最新正式版"。
    /// 默认**尊重钉死**：作者是按那个组合测过的（例如 Iris 1.7.6 钉死了 Sodium 的某个 beta），
    /// 换成别的版本可能不兼容。但钉死的是 beta 时用户会意外，所以提供开关。
    /// </param>
    public async Task<InstallPlan> ResolveAsync(
        string projectIdOrSlug,
        string gameVersion,
        IReadOnlyList<string> loaders,
        bool includePrerelease = false,
        bool ignorePinnedDependencies = false,
        CancellationToken cancellationToken = default)
    {
        var versions = await _modrinth
            .GetProjectVersionsAsync(projectIdOrSlug, gameVersion, loaders, cancellationToken)
            .ConfigureAwait(false);

        if (versions.Count == 0)
            throw new InvalidOperationException(
                $"该项目没有适用于 {gameVersion}{(loaders.Count > 0 ? " / " + string.Join(",", loaders) : "")} 的版本");

        var root = (includePrerelease ? versions : versions.Where(version => !version.IsPrerelease))
                       .FirstOrDefault()
                   ?? versions[0];

        var targets = new List<InstallTarget>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        // 广度优先：主体先入队，依赖按层展开
        var queue = new Queue<(ModrinthVersion Version, bool IsDependency, bool Pinned)>();
        queue.Enqueue((root, false, false));

        while (queue.Count > 0 && visited.Count < MaxDependencyDepth * 32)
        {
            var (version, isDependency, pinned) = queue.Dequeue();

            // 同一个项目只处理一次：一个项目可能被多个依赖同时要求
            if (!visited.Add(version.ProjectId)) continue;
            if (version.PrimaryFile is not { } file) continue;

            targets.Add(new InstallTarget(
                version.ProjectId,
                "",
                version.VersionNumber,
                file.FileName,
                file.Url,
                string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1,
                isDependency)
            {
                Pinned = pinned,
            });

            if (targets.Count > MaxDependencyDepth * 32) break;

            foreach (var dependency in version.Dependencies.Where(dependency => dependency.IsRequired))
            {
                var (resolved, wasPinned) = await ResolveDependencyAsync(
                        dependency, gameVersion, loaders, ignorePinnedDependencies, cancellationToken)
                    .ConfigureAwait(false);

                if (resolved is not null) queue.Enqueue((resolved, true, wasPinned));
            }
        }

        return new InstallPlan(await FillTitlesAsync(targets, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>把计划里的文件下载到 mods 目录。单个失败不影响其他文件。</summary>
    public async Task<InstallOutcome> InstallAsync(
        InstallPlan plan,
        string modsDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modsDirectory);

        // 资源包与光影是 .zip，不能只按 *.jar 判断"已存在"，否则每次都会重复下载
        var existing = new HashSet<string>(
            Directory.EnumerateFiles(modsDirectory).Select(Path.GetFileName)!,
            StringComparer.OrdinalIgnoreCase);

        var installed = new List<InstallTarget>();
        var skipped = new List<InstallTarget>();
        var failed = new List<(InstallTarget, string)>();

        foreach (var target in plan.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.Contains(target.FileName))
            {
                skipped.Add(target);
                continue;
            }

            var destination = Path.Combine(modsDirectory, target.FileName);

            // CurseForge 上作者禁止第三方下载时拿不到地址，这不是失败而是"不可能自动装"
            if (string.IsNullOrWhiteSpace(target.DownloadUrl))
            {
                failed.Add((target, "该文件不允许第三方下载，需到网页手动下载"));
                continue;
            }

            try
            {
                await _downloader
                    .EnsureFromAsync(new[] { target.DownloadUrl }, destination, target.Sha1)
                    .ConfigureAwait(false);

                installed.Add(target);
                existing.Add(target.FileName);
            }
            catch (Exception ex)
            {
                failed.Add((target, ex.GetBaseException().Message));
            }
        }

        return new InstallOutcome(installed, skipped, failed);
    }

    private async Task<(ModrinthVersion? Version, bool Pinned)> ResolveDependencyAsync(
        ModrinthDependency dependency,
        string gameVersion,
        IReadOnlyList<string> loaders,
        bool ignorePinned,
        CancellationToken cancellationToken)
    {
        try
        {
            // 钉死了版本就直接用（作者指定的版本组合）
            if (!ignorePinned && !string.IsNullOrWhiteSpace(dependency.VersionId))
            {
                var pinned = await _modrinth.GetVersionAsync(dependency.VersionId!, cancellationToken)
                    .ConfigureAwait(false);

                if (pinned is not null) return (pinned, true);
            }

            if (string.IsNullOrWhiteSpace(dependency.ProjectId)) return (null, false);

            var versions = await _modrinth
                .GetProjectVersionsAsync(dependency.ProjectId!, gameVersion, loaders, cancellationToken)
                .ConfigureAwait(false);

            return (versions.FirstOrDefault(version => !version.IsPrerelease) ?? versions.FirstOrDefault(), false);
        }
        catch
        {
            // 依赖解析失败不该让整个安装崩掉：留空即可，主体仍会装上
            return (null, false);
        }
    }

    private async Task<IReadOnlyList<InstallTarget>> FillTitlesAsync(
        IReadOnlyList<InstallTarget> targets,
        CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _modrinth
                .GetProjectsAsync(targets.Select(target => target.ProjectId).ToList(), cancellationToken)
                .ConfigureAwait(false);

            return targets
                .Select(target => projects.TryGetValue(target.ProjectId, out var project)
                    ? target with { ProjectTitle = project.DisplayName }
                    : target)
                .ToList();
        }
        catch
        {
            return targets;
        }
    }
}
