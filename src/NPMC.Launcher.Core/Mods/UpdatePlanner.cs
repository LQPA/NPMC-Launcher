namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// 判断"有没有更新"的纯逻辑，不碰网络也不碰磁盘，便于单测。
///
/// 规则：
///   - 候选里排除与当前同一个版本（同一个 Id）
///   - 只考虑发布时间**晚于**当前的
///   - 默认排除预发布（beta / alpha），除非调用方明确要求
///   - 取发布时间最新的一个
/// </summary>
public static class UpdatePlanner
{
    public static ModrinthVersion? FindNewer(
        ModrinthVersion current,
        IEnumerable<ModrinthVersion> candidates,
        bool includePrerelease = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => !string.Equals(candidate.Id, current.Id, StringComparison.Ordinal))
            .Where(candidate => includePrerelease || !candidate.IsPrerelease)
            .Where(candidate => candidate.PublishedAt > current.PublishedAt)
            .OrderByDescending(candidate => candidate.PublishedAt)
            .FirstOrDefault();
    }

    /// <summary>为一批本地模组算出可升级项。currentVersions 以 SHA1 为键。</summary>
    public static IReadOnlyList<ModUpdate> Plan(
        IReadOnlyList<LocalModInfo> localMods,
        IReadOnlyDictionary<string, IReadOnlyList<ModrinthVersion>> projectVersions,
        bool includePrerelease = false)
    {
        var updates = new List<ModUpdate>();

        foreach (var mod in localMods)
        {
            if (mod.MatchedVersion is not { } current) continue;

            if (!projectVersions.TryGetValue(current.ProjectId, out var candidates)) continue;
            if (candidates.Count == 0) continue;

            var newer = FindNewer(current, candidates, includePrerelease);
            if (newer is null) continue;
            if (newer.PrimaryFile is null) continue;   // 没有可下载文件就别建议升级

            updates.Add(new ModUpdate(mod.Path, mod.FileName, current, newer));
        }

        return updates;
    }
}
