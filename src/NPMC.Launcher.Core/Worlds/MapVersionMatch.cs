using System.Globalization;
using NPMC.Launcher.Core.Modpacks;

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
    /// 具体规则见 <see cref="LoaderVersionName.GameVersionFromName"/> —— 四家加载器格式不同，
    /// 那边逐家单独处理。这里只做转调，避免同一套规则出现两份。
    ///
    /// 注意：这是**纯名字解析**，拿不到的形态（如 <c>neoforge-21.1.259</c>）返回空串。
    /// 有 LauncherPaths 时应当优先用 <see cref="LoaderVersionName.ResolveGameVersion"/>
    /// 读 <c>inheritsFrom</c>，那条路四家都能拿到。
    /// </summary>
    public static string MinecraftVersionOf(string? instanceVersionId)
        => LoaderVersionName.GameVersionFromName(instanceVersionId);

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
