using System.Globalization;

namespace NPMC.Launcher.Core.Worlds;

/// <summary>地图与实例游戏版本的匹配程度。</summary>
public enum MapMatchLevel
{
    /// <summary>完全匹配：地图声明了这个确切版本。</summary>
    Exact = 0,

    /// <summary>宽松匹配：地图声明了同主次版本段的其它版本（如地图写 26.1，实例是 26.1.2）。</summary>
    SameSeries = 1,

    /// <summary>不匹配。</summary>
    None = 2,
}

/// <summary>
/// 地图（CurseForge classId 17）与实例游戏版本的匹配。
///
/// 为什么不用"版本号完全相等"：实测地图的 gameVersions 往往覆盖一整段，例如
/// Voidblock 声明 <c>26.1, 26.1.1, 26.1.2, 26.2, ..., 26.4-snapshot</c>，
/// 而用户的实例常常是其中某一个具体版本。严格相等会把大量可用组合挡在外面。
///
/// 这里的规则（用户选定：宽松匹配，按主次版本段归类）：
///   - 地图声明的版本里有与实例**完全相等**的 → <see cref="MapMatchLevel.Exact"/>；
///   - 否则，实例的"主次版本段"（1.20.1 → 1.20；26.1.2 → 26.1）与地图任一版本的
///     版本段相同 → <see cref="MapMatchLevel.SameSeries"/>；
///   - 否则不匹配。
///
/// 快照（-snapshot / -pre 等后缀）按"去掉后缀后的版本段"参与比较：
/// 地图声明 26.3-snapshot 时，26.3 的实例算同段，反之亦然。
/// </summary>
public static class MapVersionMatch
{
    /// <summary>判断地图声明的版本集合与实例版本的关系。</summary>
    /// <param name="instanceVersion">
    /// 实例的 VersionId，**可能带加载器前缀**（fabric-loader-0.18.4-1.21.11）。
    /// 内部会先抽出 MC 版本再比较，见 <see cref="MinecraftVersionOf"/>。
    /// </param>
    public static MapMatchLevel Match(IReadOnlyList<string> mapVersions, string? instanceVersion)
    {
        if (mapVersions is null || mapVersions.Count == 0) return MapMatchLevel.None;
        if (string.IsNullOrWhiteSpace(instanceVersion)) return MapMatchLevel.None;

        var target = MinecraftVersionOf(instanceVersion);
        if (target.Length == 0) return MapMatchLevel.None;

        foreach (var version in mapVersions)
        {
            if (string.IsNullOrWhiteSpace(version)) continue;

            // 两侧都去掉快照后缀再比，否则「地图写 26.3-snapshot、实例是 26.3」
            // 会因为字符串不等而落到同段匹配（结果对，但等级不准）
            if (string.Equals(StripSuffix(version.Trim()), StripSuffix(target), StringComparison.OrdinalIgnoreCase))
                return MapMatchLevel.Exact;
        }

        var targetSeries = SeriesOf(target);

        foreach (var version in mapVersions)
        {
            if (string.IsNullOrWhiteSpace(version)) continue;

            var series = SeriesOf(version);
            if (series.Length == 0) continue;

            if (string.Equals(series, targetSeries, StringComparison.OrdinalIgnoreCase))
                return MapMatchLevel.SameSeries;
        }

        return MapMatchLevel.None;
    }

    /// <summary>
    /// 取"主次版本段"：1.20.1 → 1.20，26.1.2 → 26.1，
    /// 26.3-snapshot → 26.3，1.8.9 → 1.8。
    ///
    /// 只有一段时（如 "26" 或空前缀）返回它自己；无法解析时返回空串。
    /// </summary>
    public static string SeriesOf(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";

        var cleaned = StripSuffix(version.Trim());
        if (cleaned.Length == 0) return "";

        var parts = cleaned.Split('.');
        if (parts.Length == 0) return "";

        // 主版本必须是数字，否则不是版本号
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return "";

        // 只有一个主版本段时就用它自己
        if (parts.Length == 1) return parts[0];

        // 第二段可能不是数字（极少见），那就只保留主版本
        return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? parts[0] + "." + parts[1]
            : parts[0];
    }

    /// <summary>
    /// 从实例的 VersionId 里取出 Minecraft 版本号。
    ///
    /// 实测实例目录的 version 字段是**加载器版本名**而不是纯 MC 版本，形态非常多：
    ///   <c>26.3</c>、<c>1.21.1</c>、<c>1.7.10</c>                      ← 纯版本，直接用
    ///   <c>fabric-loader-0.18.4-1.21.11</c>                          ← 取 1.21.11
    ///   <c>1.20.1-forge-47.1.106</c>                                 ← 取开头的 1.20.1
    ///   <c>1.9-forge1.9-12.16.1.1938-1.9.0</c>                       ← 取 1.9
    ///   <c>1.7.10-Forge10.13.4.1614-1.7.10</c>                       ← 取 1.7.10
    ///   <c>quilt-loader-0.30.1-26.3</c>                              ← 取 26.3
    ///   <c>neoforge-21.1.259</c>、<c>neoforge-26.2.0.93</c>          ← 只有加载器版本，取不到
    ///   <c>22w13oneblockatatime</c>                                  ← 愚人节快照，取不到
    ///
    /// 最麻烦的是 <c>neoforge-26.1.2.124</c>：它的加载器版本号看起来和 MC 版本一模一样。
    /// 所以不能"只按形态挑第一个像的"，必须**先排除加载器自己的版本段**。
    /// </summary>
    public static string MinecraftVersionOf(string? instanceVersionId)
    {
        if (string.IsNullOrWhiteSpace(instanceVersionId)) return "";

        var value = instanceVersionId.Trim();

        // 纯版本号（可能带 -snapshot 之类后缀）
        if (LooksLikeMinecraftVersion(value)) return StripSuffix(value);

        // 用 - 切成若干段
        // fabric-loader-0.18.4-1.21.11 → ["fabric","loader","0.18.4","1.21.11"]
        var segments = value.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        // 加载器名后面紧跟的那一段是**加载器版本**，即使它长得像 MC 版本也必须跳过：
        //   neoforge-26.1.2.124 → 跳过 "26.1.2.124"（形似但实为加载器版本）
        //   forge-47.1.106      → 跳过 "47.1.106"
        var loaderPrefixes = new[] { "neoforge", "forge", "fabric", "quilt", "liteloader", "rift" };
        var skip = new HashSet<int>();

        for (var i = 0; i < segments.Length; i++)
        {
            var isLoaderName = loaderPrefixes.Any(prefix =>
                string.Equals(segments[i], prefix, StringComparison.OrdinalIgnoreCase)
                || segments[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            // "loader"/"loader-0.18.4" 这种也一并跳过（fabric-loader-0.18.4 两段）
            if (!isLoaderName && !segments[i].Contains("loader", StringComparison.OrdinalIgnoreCase)) continue;

            // 紧跟在加载器名后面的一段是版本号，跳过
            if (i + 1 < segments.Length) skip.Add(i + 1);
        }

        for (var i = 0; i < segments.Length; i++)
        {
            if (skip.Contains(i)) continue;
            if (LooksLikeMinecraftVersion(segments[i])) return StripSuffix(segments[i]);
        }

        return "";
    }

    /// <summary>
    /// 看起来像 Minecraft 版本。
    ///
    /// 实测实例的 VersionId 里同时混着两种"版本号"，必须区分开：
    ///   - MC 版本：<c>1.21.11</c>、<c>1.20.1</c>、<c>26.1.2</c>、<c>1.8.9</c>
    ///   - 加载器版本：<c>neoforge-21.1.259</c>、<c>47.1.106</c>
    ///
    /// 规则：主版本 1~99 且次段是个小数字（MC 的次段最大到 21 左右，
    /// 而加载器版本一般次段也是小数 —— 所以真正靠的是调用方先剔除加载器段）。
    /// </summary>
    private static bool LooksLikeMinecraftVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var cleaned = StripSuffix(value.Trim());
        var parts = cleaned.Split('.');

        // MC 版本最多三段：1.21.11 / 26.1.2；四段以上是加载器版本（26.1.2.124）
        if (parts.Length < 2 || parts.Length > 3) return false;

        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
        }

        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], CultureInfo.InvariantCulture);

        // 1.x 线：1.21.11 / 1.20.1 / 1.12.2 / 1.7.10（次段 7~22）
        if (major == 1) return minor is >= 7 and <= 25;

        // 2026 起的快照线：26.1 / 26.2 / 26.3（次段 1~9）
        return major is >= 26 and <= 40 && minor is >= 1 and <= 9;
    }

    /// <summary>去掉 -snapshot / -pre / -rc 之类的后缀，只留版本号本身。</summary>
    private static string StripSuffix(string version)
    {
        var index = version.IndexOfAny(new[] { '-', '+' });
        return index < 0 ? version : version[..index];
    }

    /// <summary>给人看的说明（界面上显示"支持 26.1~26.3"这类摘要）。</summary>
    public static string DescribeMatch(IReadOnlyList<string> mapVersions)
    {
        if (mapVersions is null || mapVersions.Count == 0) return "未声明支持的版本";

        var series = new List<string>();
        foreach (var version in mapVersions)
        {
            var value = SeriesOf(version);
            if (value.Length == 0 || series.Contains(value)) continue;
            series.Add(value);
        }

        return series.Count == 0
            ? "未声明支持的版本"
            : series.Count == 1
                ? $"支持 {series[0]}"
                : $"支持 {string.Join("、", series)}";
    }
}
