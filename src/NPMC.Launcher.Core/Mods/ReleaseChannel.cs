namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// 版本的发布通道。两个平台的表示方式不同，统一归一化成这三个值：
///   Modrinth：version_type = "release" / "beta" / "alpha"
///   CurseForge：releaseType = 1 / 2 / 3
///
/// 归一化放在 Core 里而不是界面里，是为了能被单测直接覆盖 ——
/// "界面里 R/B/A 会不会标错"这种问题不该靠碰运气找一个恰好有 alpha 的项目来验证。
/// </summary>
public enum ReleaseChannel
{
    Release = 0,
    Beta = 1,
    Alpha = 2,
}

public static class ReleaseChannelExtensions
{
    /// <summary>Modrinth 的 version_type → 通道。无法识别的一律按正式版处理。</summary>
    public static ReleaseChannel FromModrinthType(string? versionType) => versionType?.Trim().ToLowerInvariant() switch
    {
        "alpha" => ReleaseChannel.Alpha,
        "beta" => ReleaseChannel.Beta,
        _ => ReleaseChannel.Release,
    };

    /// <summary>CurseForge 的 releaseType → 通道。1 正式 / 2 beta / 3 alpha。</summary>
    public static ReleaseChannel FromCurseForgeReleaseType(int releaseType) => releaseType switch
    {
        2 => ReleaseChannel.Beta,
        3 => ReleaseChannel.Alpha,
        _ => ReleaseChannel.Release,
    };

    /// <summary>徽标上的单个字母。</summary>
    public static string Badge(this ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Alpha => "A",
        ReleaseChannel.Beta => "B",
        _ => "R",
    };

    public static string DisplayName(this ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Alpha => "Alpha 版",
        ReleaseChannel.Beta => "Beta 版",
        _ => "正式版",
    };

    /// <summary>是否属于"非正式发布"，用于"只装正式版"这类过滤。</summary>
    public static bool IsPrerelease(this ReleaseChannel channel) => channel != ReleaseChannel.Release;
}
