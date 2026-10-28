using System.Text.RegularExpressions;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>
/// 导出整合包时的"哪些文件放进 overrides"规则（参照 PCL 的导出配置格式）。
///
/// 语法（`export-rules.txt`，一行一条）：
///   - `config/`            包含目录（结尾 `/` 表示目录）
///   - `options.txt`        包含单个文件（相对实例目录）
///   - `!mods/*.disabled`   排除（`!` 开头），后出现的规则覆盖先出现的
///   - `*` `?` 通配；`*` 可以跨越 `/`
///   - 空行与 `#` 开头的行是注释
///   - `=====` 之后的行是**绝对路径**的额外文件：结尾带 `\`/`/` 表示文件夹，
///     与 PCL 一样直接放到压缩包根目录
/// </summary>
public static class ExportRules
{
    /// <summary>规则与额外路径的分隔行。</summary>
    public const string ExternalSectionMarker = "=====";

    /// <summary>首次运行时写入的默认规则：把实例里"该带走"的配置带全，日志/存档等默认排除。</summary>
    public static IReadOnlyList<string> DefaultLines => new[]
    {
        "# 每行一条：相对实例目录；! 开头表示排除；支持 * ? 通配；靠后的行覆盖靠前的",
        "# 以 / 结尾表示目录；===== 之后的行是绝对路径的额外文件（结尾 / 表示文件夹）",
        "",
        "# 游戏本体设置",
        "options.txt",
        "configureddefaults/",
        "optionsof.txt",
        "optionsshaders.txt",
        "",
        "# Mod 配置",
        "config/",
        "defaultconfigs/",
        "",
        "# 默认排除（存档与日志不进整合包）",
        "!saves/",
        "!logs/",
        "!crash-reports/",
        "!*.log",
        "!*.dat_old",
    };

    /// <summary>读规则文件；文件不存在时返回默认规则（不落盘，由调用方决定是否写入）。</summary>
    public static IReadOnlyList<string> Load(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : DefaultLines;
        }
        catch
        {
            return DefaultLines;
        }
    }

    /// <summary>拆成"相对规则"与"绝对额外路径"两部分，并去掉注释/空行。</summary>
    public static (IReadOnlyList<string> Rules, IReadOnlyList<string> External) Split(IReadOnlyList<string> lines)
    {
        var rules = new List<string>();
        var external = new List<string>();
        var inExternal = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith(ExternalSectionMarker, StringComparison.Ordinal))
            {
                inExternal = true;
                continue;
            }

            (inExternal ? external : rules).Add(line);
        }

        return (rules, external);
    }

    /// <summary>
    /// 判断一个相对路径（`config/mod.toml`，用 `/`）是否应打包。
    /// 从前往后匹配，最后一条命中的规则说了算；没有任何规则命中 = 不打包。
    /// </summary>
    public static bool IsIncluded(string relativePath, IReadOnlyList<string> rules)
    {
        var included = false;

        foreach (var rule in rules)
        {
            var exclude = rule.StartsWith('!');
            var pattern = exclude ? rule[1..].Trim() : rule;

            if (pattern.Length == 0) continue;
            if (!Matches(relativePath, pattern)) continue;

            included = !exclude;
        }

        return included;
    }

    /// <summary>目录规则（结尾 `/`）匹配其下所有文件；其余按通配符匹配整个相对路径。</summary>
    public static bool Matches(string relativePath, string pattern)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        if (pattern.EndsWith('/'))
        {
            // 去掉结尾的 '/' 作为目录前缀；`config/` 匹配 config/mod.toml，
            // 但不该匹配 configs/ 或 configfoo.txt
            var prefix = pattern[..^1];
            return string.Equals(normalized, prefix, StringComparison.OrdinalIgnoreCase)
                   || normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        // 末尾是通配符时按前缀匹配（`mods/*` 也要命中 mods 下的深层文件）
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(normalized, regex, RegexOptions.IgnoreCase);
    }
}
