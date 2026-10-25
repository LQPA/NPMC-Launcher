namespace NPMC.Launcher.Core.Models;

/// <summary>一个已确认的愚人节版本：版本号 + 年份 + 主题。</summary>
public sealed record AprilFoolsRelease(string Id, int Year, string Theme)
{
    /// <summary>下拉里显示的附加文字，例如 "2024 · 毒马铃薯"。</summary>
    public string ThemeText => $"{Year} · {Theme}";
}

/// <summary>
/// 愚人节版本识别。
///
/// 为什么必须靠清单：Mojang 的 version_manifest 里**没有任何字段**标识愚人节版本，
/// 它们的 <c>type</c> 就是 <c>snapshot</c>，与普通快照完全无法区分。
/// 唯一可靠的信号是版本号本身（发布日则几乎都在 4 月 1 日）。
///
/// 因此这里维护一份"版本号 → 年份 + 主题"的清单，
/// 并保留"4 月 1 日发布的快照"作为兜底，覆盖将来新增的年份。
/// </summary>
public static class AprilFoolsVersions
{
    /// <summary>已确认的愚人节版本（Java 版 2015–2025；2017、2018、2021 没有愚人节版本）。</summary>
    public static IReadOnlyList<AprilFoolsRelease> Known { get; } = new[]
    {
        new AprilFoolsRelease("15w14a", 2015, "爱之石"),
        new AprilFoolsRelease("1.RV-Pre1", 2016, "现实更新"),
        new AprilFoolsRelease("3D Shareware v1.34", 2019, "3D 共享软件"),
        new AprilFoolsRelease("20w14infinite", 2020, "无限维度"),
        new AprilFoolsRelease("22w13oneblockatatime", 2022, "一个方块"),
        new AprilFoolsRelease("23w13a_or_b", 2023, "投票更新"),
        new AprilFoolsRelease("24w14potato", 2024, "毒马铃薯"),
        new AprilFoolsRelease("25w14craftmine", 2025, "合成地雷"),
    };

    private static readonly Dictionary<string, AprilFoolsRelease> ById =
        Known.ToDictionary(release => release.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> KnownIds => ById.Keys;

    /// <summary>按版本号找对应的愚人节版本；不是则返回 null。</summary>
    public static AprilFoolsRelease? Find(string id)
        => !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id, out var release) ? release : null;

    /// <summary>
    /// 是否为愚人节版本。
    /// </summary>
    /// <param name="id">版本号。</param>
    /// <param name="releaseTime">发布日期；未知时传 default，此时只靠清单判断。</param>
    /// <param name="type">清单里的类型。日期兜底**必须**看到它是 snapshot 才生效。</param>
    /// <remarks>
    /// 日期兜底不能无条件使用：正式版也可能在 4 月 1 日发布（例如 26.1.1），
    /// 那时会被误判成愚人节，连带从"正式版"列表里消失。
    /// 因此只有明确知道类型是 snapshot 时才按日期判断。
    /// </remarks>
    public static bool IsAprilFools(string id, DateTimeOffset releaseTime = default, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (ById.ContainsKey(id)) return true;

        if (releaseTime == default) return false;

        if (!string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase)) return false;

        // 兜底：将来新增的愚人节版本仍在 4 月 1 日发布
        return releaseTime is { Month: 4, Day: 1 };
    }
}
