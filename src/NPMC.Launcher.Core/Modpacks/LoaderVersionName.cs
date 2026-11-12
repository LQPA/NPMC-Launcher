using System.Globalization;
using System.Text.Json;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Core.Modpacks;

/// <summary>从版本名里识别出来的加载器与版本信息。</summary>
public sealed record LoaderVersionInfo(
    string LoaderKey,
    string LoaderVersion)
{
    public static LoaderVersionInfo Vanilla { get; } = new("vanilla", "");
}

/// <summary>
/// 四家模组加载器（Fabric / Forge / NeoForge / Quilt）**分别**解析版本名的统一入口。
///
/// 背景：实例的 <c>VersionId</c> 不是 Minecraft 版本，而是各加载器自己的 profile 名，
/// 四家的格式各不相同，且都来自各自官方 API，启动器无法也没必要去改名：
///
/// | 加载器 | 版本名形态（实测 E:\aaaa） |
/// | --- | --- |
/// | 原版 | <c>26.3</c>、<c>1.21.11</c>、<c>1.7.10</c> |
/// | Fabric | <c>fabric-loader-0.18.4-1.21.11</c>（加载器在前） |
/// | Quilt | <c>quilt-loader-0.30.1-26.3</c>（加载器在前） |
/// | Forge（新） | <c>26.3-forge-66.1.0</c>（MC 在前） |
/// | Forge（旧） | <c>1.7.10-Forge10.13.4.1614-1.7.10</c>、<c>1.9-forge1.9-12.16.1.1938-1.9.0</c> |
/// | NeoForge | <c>neoforge-21.1.259</c>、<c>neoforge-26.1.2.124</c>（**名字里没有 MC 版本**） |
///
/// 两点设计取舍：
///   1. **优先读版本 JSON 里的 <c>inheritsFrom</c>** —— 四家都准确写出父版本，
///      连 NeoForge 都能拿到（实测 neoforge-21.1.259 → 1.21.1）。名字解析只作兜底，
///      因为名字格式会随加载器版本变化，而 inheritsFrom 是启动必需的字段。
///   2. 名字解析**逐家单独处理**，不用"找一个像版本号的段"这种通用启发式：
///      <c>neoforge-26.1.2.124</c> 的加载器版本号看起来和 MC 版本一模一样，
///      通用启发式必然误判。
/// </summary>
public static class LoaderVersionName
{
    private static readonly string[] LoaderKeys = { "fabric", "quilt", "forge", "neoforge" };

    /// <summary>
    /// 从版本名判断是哪个加载器及加载器版本。四家分别匹配，识别不出来就是原版。
    /// </summary>
    public static LoaderVersionInfo DetectLoader(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return LoaderVersionInfo.Vanilla;

        var value = versionId.Trim();
        var lower = value.ToLowerInvariant();

        // ---- Fabric：fabric-loader-<loader>-<mc>，加载器版本形如 0.18.4 / 0.20.1-beta.3 ----
        if (lower.Contains("fabric-loader", StringComparison.Ordinal))
        {
            var rest = lower[(lower.IndexOf("fabric-loader", StringComparison.Ordinal) + "fabric-loader".Length)..]
                .TrimStart('-', '_');

            var loader = LeadingVersion(rest);
            return new LoaderVersionInfo("fabric", loader);
        }

        // ---- Quilt：quilt-loader-<loader>-<mc>，加载器版本形如 0.30.1 / 0.31.0-beta.4 ----
        if (lower.Contains("quilt-loader", StringComparison.Ordinal))
        {
            var rest = lower[(lower.IndexOf("quilt-loader", StringComparison.Ordinal) + "quilt-loader".Length)..]
                .TrimStart('-', '_');

            var loader = LeadingVersion(rest);
            return new LoaderVersionInfo("quilt", loader);
        }

        // ---- NeoForge：neoforge-<loader>，加载器版本形如 21.1.259 / 26.1.2.124 ----
        if (lower.StartsWith("neoforge", StringComparison.Ordinal))
        {
            var rest = lower["neoforge".Length..].TrimStart('-', '_');
            return new LoaderVersionInfo("neoforge", LeadingVersion(rest));
        }

        // ---- Forge：三种历史形态 ----
        //   >1.12： <mc>-forge-<loader>        例 26.3-forge-66.1.0
        //   <1.13： <mc>-Forge<loader>         例 1.7.10-Forge10.13.4.1614-1.7.10
        //   特殊：  <mc>-forge<mc>-<loader>-<mc> 例 1.9-forge1.9-12.16.1.1938-1.9.0
        if (lower.Contains("forge", StringComparison.Ordinal))
        {
            var loader = ForgeLoaderVersion(value);
            if (loader.Length > 0) return new LoaderVersionInfo("forge", loader);
        }

        return LoaderVersionInfo.Vanilla;
    }

    /// <summary>
    /// Forge 的加载器版本。Forge 的版本名形态最乱，逐个形态单独处理：
    ///   - <c>26.3-forge-66.1.0</c>            → 66.1.0（取 forge- 后面那段）
    ///   - <c>1.20.1-forge-47.1.106</c>        → 47.1.106
    ///   - <c>1.7.10-Forge10.13.4.1614-1.7.10</c> → 10.13.4.1614（Forge 紧跟的数字）
    ///   - <c>1.9-forge1.9-12.16.1.1938-1.9.0</c> → 12.16.1.1938（跳过紧贴的 MC 版本）
    /// </summary>
    private static string ForgeLoaderVersion(string value)
    {
        var lower = value.ToLowerInvariant();

        // 定位 "forge" 出现的位置（跳过可能的 "neoforge"）
        for (var index = 0; index < lower.Length; )
        {
            var found = lower.IndexOf("forge", index, StringComparison.Ordinal);
            if (found < 0) return "";

            index = found + "forge".Length;
            var rest = lower[index..].TrimStart('-', '_');
            if (rest.Length == 0) continue;

            // 形如 1.9-forge1.9-12.16.1.1938-1.9.0：forge 后面紧跟的若是"像 MC 版本"的数字，
            // 那个其实是重复写的 MC 版本，真正的加载器版本在下一个 '-' 之后
            var segment = LeadingVersion(rest);
            if (segment.Length == 0) continue;

            if (LooksLikeMinecraftVersion(segment))
            {
                var dash = rest.IndexOf('-');
                if (dash >= 0)
                {
                    var next = LeadingVersion(rest[(dash + 1)..]);
                    if (next.Length > 0 && !LooksLikeMinecraftVersion(next)) return next;
                    if (next.Length > 0) return next;
                }
            }

            return segment;
        }

        return "";
    }

    /// <summary>
    /// 逐家解析版本名里的**游戏版本**（名字兜底用；优先用 <see cref="ReadGameVersionAsync"/>）。
    /// </summary>
    public static string GameVersionFromName(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return "";

        var value = versionId.Trim();

        // 原版：名字本身就是 MC 版本（可带 -snapshot-6 之类后缀）
        if (LooksLikeMinecraftVersion(value)) return StripSuffix(value);

        var lower = value.ToLowerInvariant();
        var segments = value.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        switch (DetectLoader(value).LoaderKey)
        {
            case "fabric":
            case "quilt":
            {
                // fabric-loader-0.18.4-1.21.11 → 加载器版本之后的那段就是 MC 版本。
                // 不能按"与 LoaderVersion 字符串相等"来定位：加载器版本带后缀时
                // （quilt-loader-0.31.0-beta.4-1.21.1）会被切成两段，那段对不上。
                // 改为：找到加载器名之后的第一个"像 MC 版本"的段，跳过加载器版本本身。
                var loaderName = DetectLoader(value).LoaderKey == "quilt" ? "quilt-loader" : "fabric-loader";
                var afterLoader = lower[(lower.IndexOf(loaderName, StringComparison.Ordinal) + loaderName.Length)..]
                    .TrimStart('-', '_');

                var parts = afterLoader.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    // 第一段是加载器版本（可能是 0.31.0 或 0.31.0-beta.4 的前半），从第二段开始找
                    if (i == 0) continue;
                    if (LooksLikeMinecraftVersion(parts[i])) return StripSuffix(parts[i]);
                }

                break;
            }

            case "forge":
            {
                // Forge 的 MC 版本在名字**最前面**（1.20.1-forge-47.1.106、1.7.10-Forge10.13.4.1614-1.7.10）
                if (segments.Length > 0 && LooksLikeMinecraftVersion(segments[0]))
                    return StripSuffix(segments[0]);

                break;
            }

            case "neoforge":
            {
                // neoforge-21.1.259 / neoforge-26.1.2.124 的**名字里没有 MC 版本**：
                // 那一段是加载器版本，即使它形似 MC 版本也不能返回。
                return "";
            }
        }

        return "";
    }

    /// <summary>
    /// 读版本 JSON 的 <c>inheritsFrom</c> 拿真正的游戏版本（四家加载器都能拿到）。
    /// 读不到时返回空串，调用方再退回 <see cref="GameVersionFromName"/>。
    ///
    /// 只读一层：实例指向加载器 profile，它的 inheritsFrom 就是 MC 版本；
    /// 少数情况再加一层（加载器 profile 继承另一个加载器 profile），最多走 3 层防环。
    /// </summary>
    public static string ReadGameVersion(LauncherPaths paths, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return "";

        var current = versionId.Trim();

        for (var depth = 0; depth < 3; depth++)
        {
            var jsonPath = paths.VersionJson(current);
            if (!File.Exists(jsonPath)) return "";

            string? parent;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = document.RootElement;
                parent = root.TryGetProperty("inheritsFrom", out var element)
                         && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;
            }
            catch
            {
                return "";
            }

            // 没有父版本 = 链尾，自己就是 MC 版本
            if (string.IsNullOrWhiteSpace(parent)) return current;

            current = parent.Trim();
        }

        return current;
    }

    /// <summary>
    /// 综合取值：先读 <c>inheritsFrom</c>，失败再按四家规则从名字解析。
    /// </summary>
    public static string ResolveGameVersion(LauncherPaths paths, string? versionId)
    {
        var fromJson = ReadGameVersion(paths, versionId);
        if (fromJson.Length > 0) return fromJson;

        return GameVersionFromName(versionId);
    }

    /// <summary>加载器显示名（界面/日志用）。</summary>
    public static string DisplayName(string loaderKey) => loaderKey switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "vanilla" => "原版",
        _ => loaderKey,
    };

    // ---- 内部工具 ----

    /// <summary>取字符串开头的一段版本号（数字与点，允许一个 - 后缀如 beta.4）。</summary>
    private static string LeadingVersion(string value)
    {
        var end = 0;
        while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.')) end++;

        var head = value[..end].TrimEnd('.');
        if (head.Length == 0) return "";

        // 带上后缀（0.31.0-beta.4），但**不能**把后面的 MC 版本吞进来：
        //   fabric-loader-0.20.1-1.15 → 头是 0.20.1，后缀 -1.15 其实是 MC 版本，必须断开
        //   quilt-loader-0.31.0-beta.4-1.21.1 → 后缀 beta.4 之后才是 MC 版本
        if (end < value.Length && value[end] == '-')
        {
            var suffixEnd = end + 1;
            while (suffixEnd < value.Length
                   && (char.IsLetterOrDigit(value[suffixEnd]) || value[suffixEnd] == '.'))
            {
                suffixEnd++;
            }

            var suffix = value[end..suffixEnd];   // 含前导 '-'

            // 后缀本身像 MC 版本（-1.15 / -26.3）说明那是下一个版本号，不属于加载器版本
            if (!LooksLikeMinecraftVersion(suffix.TrimStart('-'))) head += suffix;
        }

        return head;
    }

    /// <summary>去掉 -snapshot / -pre / -rc 之类的后缀，只留版本号本身。</summary>
    private static string StripSuffix(string version)
    {
        var index = version.IndexOfAny(new[] { '-', '+' });
        return index < 0 ? version : version[..index];
    }

    /// <summary>
    /// 看起来像 Minecraft 版本（1.x 线或 2026 起的 26.x 快照线）。
    ///
    /// 上界用来排除加载器版本：neoforge 21.1 / forge 47.1 的主版本都远超 MC 的取值范围。
    /// </summary>
    private static bool LooksLikeMinecraftVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var cleaned = StripSuffix(value.Trim());
        var parts = cleaned.Split('.');
        if (parts.Length < 2 || parts.Length > 3) return false;

        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
        }

        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], CultureInfo.InvariantCulture);

        if (major == 1) return minor is >= 7 and <= 25;
        return major is >= 26 and <= 40 && minor is >= 1 and <= 9;
    }
}
