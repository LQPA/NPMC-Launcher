namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// 判断"有没有更新"的纯逻辑，不碰网络也不碰磁盘，便于单测。
///
/// 规则：
///   - 候选里排除与当前同一个版本（同一平台 + 同一项目 + 同一版本标签）
///   - 只考虑发布时间**晚于**当前的
///   - 默认排除预发布（beta / alpha），除非调用方明确要求
///   - 取发布时间最新的一个
/// </summary>
public static class UpdatePlanner
{
    public static UpdateCandidate? FindNewer(
        UpdateCandidate current,
        IEnumerable<UpdateCandidate> candidates,
        bool includePrerelease = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => !candidate.IsSameVersion(current))
            .Where(candidate => includePrerelease || !candidate.IsPrerelease)
            .Where(candidate => candidate.PublishedAt > current.PublishedAt)
            .OrderByDescending(candidate => candidate.PublishedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// 为一批本地模组算出可升级项。
    /// projectVersions 以 <see cref="UpdateProjectKey"/> 为键（含来源，避免跨平台撞号）。
    /// </summary>
    public static IReadOnlyList<ModUpdate> Plan(
        IReadOnlyList<LocalModInfo> localMods,
        IReadOnlyDictionary<UpdateProjectKey, IReadOnlyList<UpdateCandidate>> projectVersions,
        bool includePrerelease = false)
    {
        var updates = new List<ModUpdate>();

        foreach (var mod in localMods)
        {
            if (mod.Matched is not { } current) continue;
            if (!projectVersions.TryGetValue(current.Key, out var candidates)) continue;

            var newer = FindNewer(current, candidates, includePrerelease);
            if (newer is null) continue;

            // 拿不到下载地址就不要建议升级 —— 那是"需前往网页下载"的情况
            if (string.IsNullOrWhiteSpace(newer.DownloadUrl)) continue;

            updates.Add(new ModUpdate(mod.Path, mod.FileName, current, newer));
        }

        return updates;
    }
}
