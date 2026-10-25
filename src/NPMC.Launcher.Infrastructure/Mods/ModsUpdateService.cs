using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Hashing;
using NPMC.Launcher.Infrastructure.Modrinth;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>一次更新检查的结果。</summary>
public sealed record ModUpdateReport
{
    public required IReadOnlyList<ModUpdate> Updates { get; init; }
    public required IReadOnlyList<LocalModInfo> LocalMods { get; init; }

    public required int ModrinthMatches { get; init; }
    public required int CurseForgeMatches { get; init; }

    /// <summary>两个平台都认领不到的文件数。</summary>
    public required int UnmatchedCount { get; init; }

    public required IReadOnlyList<string> Loaders { get; init; }

    /// <summary>CurseForge 未启用或失败的原因（启用且成功时为 null）。</summary>
    public string? CurseForgeNote { get; init; }

    public int TotalFiles => LocalMods.Count;
    public int MatchedCount => ModrinthMatches + CurseForgeMatches;
}

/// <summary>
/// 检查模组更新的共享实现。
///
/// 为什么放在 Infrastructure 而不是各自的 UI/CLI：
///   这段逻辑（哈希 → 反查 → 按版本与加载器取列表 → 判定）在命令行和界面里是同一件事。
///   之前复制了两份，加 CurseForge 时会同时膨胀，行为很容易走偏。
///
/// 策略：**Modrinth 优先，CurseForge 只补它认领不到的文件**。
/// Modrinth 不需要 key 也没有第三方下载限制，能认领就不必再问第二个平台。
/// </summary>
public sealed class ModsUpdateService
{
    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient? _curseForge;

    public ModsUpdateService(HttpClient http)
    {
        _modrinth = new ModrinthClient(http);

        if (CurseForgeClient.TryResolveApiKey(out var apiKey, out var problem) && apiKey is not null)
            _curseForge = new CurseForgeClient(http, apiKey);
        else
            CurseForgeNote = problem ?? "未配置";
    }

    public bool CurseForgeEnabled => _curseForge is not null;

    /// <summary>CurseForge 未启用时的原因；启用后仍可能因为请求失败而写入此处。</summary>
    public string? CurseForgeNote { get; private set; }

    /// <summary>
    /// 检查一批 jar 是否有新版本。
    /// </summary>
    /// <param name="jarPaths">要检查的 jar 绝对路径。</param>
    /// <param name="gameVersion">实例的游戏版本，用于过滤（避免把 1.21 的版本推给 1.20.1）。</param>
    /// <param name="loaders">检测到的加载器（fabric / forge / neoforge / quilt）；空表示原版/未知，不按加载器过滤。</param>
    /// <param name="includePrerelease">是否接受 beta / alpha。</param>
    /// <param name="curseForgeOnly">跳过 Modrinth，全部交给 CurseForge（诊断指纹用）。</param>
    public async Task<ModUpdateReport> CheckAsync(
        IReadOnlyList<string> jarPaths,
        string gameVersion,
        IReadOnlyList<string> loaders,
        bool includePrerelease = false,
        bool curseForgeOnly = false,
        CancellationToken cancellationToken = default)
    {
        // 1) 本地 SHA1
        var hashByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in jarPaths)
        {
            try
            {
                hashByPath[path] = await FileHashing.Sha1Async(path, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 读不到就跳过这个文件
            }
        }

        var matchedCandidates = new Dictionary<string, UpdateCandidate>(StringComparer.OrdinalIgnoreCase);
        var projectVersions = new Dictionary<UpdateProjectKey, IReadOnlyList<UpdateCandidate>>();
        var modrinthMatches = 0;

        // 2) Modrinth
        if (!curseForgeOnly && hashByPath.Count > 0)
        {
            var matched = await _modrinth
                .GetVersionsByHashesAsync(hashByPath.Values.ToList(), cancellationToken)
                .ConfigureAwait(false);

            foreach (var pair in matched)
            {
                if (pair.Value.ToCandidate() is not { } candidate) continue;
                matchedCandidates[pair.Key] = candidate;
                modrinthMatches++;
            }

            foreach (var key in matchedCandidates.Values.Select(candidate => candidate.Key).Distinct())
            {
                try
                {
                    var versions = await _modrinth
                        .GetProjectVersionsAsync(key.ProjectId, gameVersion, loaders, cancellationToken)
                        .ConfigureAwait(false);

                    projectVersions[key] = versions
                        .Select(version => version.ToCandidate())
                        .Where(candidate => candidate is not null)
                        .Select(candidate => candidate!)
                        .ToList();
                }
                catch
                {
                    // 单个项目失败不影响其他项目
                }
            }
        }

        // 3) CurseForge 补缺口
        var curseForgeMatches = 0;

        if (_curseForge is not null)
        {
            var pending = curseForgeOnly
                ? hashByPath.ToList()
                : hashByPath.Where(pair => !matchedCandidates.ContainsKey(pair.Value)).ToList();

            if (pending.Count > 0)
            {
                try
                {
                    curseForgeMatches = await FillFromCurseForgeAsync(
                        pending, hashByPath, matchedCandidates, projectVersions,
                        gameVersion, loaders, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CurseForgeNote = $"查询失败：{ex.GetBaseException().Message}";
                }
            }
        }

        // 4) 判定
        var localMods = hashByPath
            .Select(pair => new LocalModInfo
            {
                Path = pair.Key,
                FileName = Path.GetFileName(pair.Key),
                Sha1 = pair.Value,
                Matched = matchedCandidates.GetValueOrDefault(pair.Value),
            })
            .ToList();

        var updates = UpdatePlanner.Plan(localMods, projectVersions, includePrerelease);

        return new ModUpdateReport
        {
            Updates = updates,
            LocalMods = localMods,
            ModrinthMatches = modrinthMatches,
            CurseForgeMatches = curseForgeMatches,
            UnmatchedCount = localMods.Count(mod => mod.Matched is null),
            Loaders = loaders,
            CurseForgeNote = CurseForgeNote,
        };
    }

    private async Task<int> FillFromCurseForgeAsync(
        List<KeyValuePair<string, string>> pending,
        Dictionary<string, string> hashByPath,
        Dictionary<string, UpdateCandidate> matchedCandidates,
        Dictionary<UpdateProjectKey, IReadOnlyList<UpdateCandidate>> projectVersions,
        string gameVersion,
        IReadOnlyList<string> loaders,
        CancellationToken cancellationToken)
    {
        // CurseForge 靠指纹识别，先算出来
        var pathByFingerprint = new Dictionary<uint, string>();
        foreach (var pair in pending)
        {
            try
            {
                var fingerprint = await MurmurHash2
                    .ComputeFileAsync(pair.Key, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                pathByFingerprint[fingerprint] = pair.Key;
            }
            catch
            {
                // 单个文件失败就跳过
            }
        }

        if (pathByFingerprint.Count == 0) return 0;

        var matches = await _curseForge!
            .GetMatchesAsync(pathByFingerprint.Keys.ToList(), cancellationToken)
            .ConfigureAwait(false);

        var claimed = 0;

        foreach (var match in matches)
        {
            if (!pathByFingerprint.TryGetValue(match.Fingerprint, out var path)) continue;
            if (!hashByPath.TryGetValue(path, out var sha1)) continue;

            var current = match.File.ToCandidate();
            matchedCandidates[sha1] = current;
            claimed++;

            // 该项目在当前游戏版本/加载器下的文件，按发布时间从新到旧
            IReadOnlyList<CurseForgeFile> files;
            try
            {
                files = await _curseForge
                    .GetFilesAsync(match.File.ModId, gameVersion, loaders, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var newest = files.FirstOrDefault();
            if (newest is null) continue;

            // 文件列表通常不带下载地址；作者禁止第三方下载时这里会拿到 null，
            // 于是判定器不会建议升级（而不是给一个点了会失败的按钮）。
            var url = newest.DownloadUrl
                      ?? await _curseForge.TryGetDownloadUrlAsync(newest.ModId, newest.FileId, cancellationToken)
                          .ConfigureAwait(false);

            projectVersions[current.Key] = new[]
            {
                current,
                (newest with { DownloadUrl = url }).ToCandidate(),
            };
        }

        return claimed;
    }
}
