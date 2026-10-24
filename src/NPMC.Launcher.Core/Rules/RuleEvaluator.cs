using System.Text.RegularExpressions;
using NPMC.Launcher.Core.Models;

namespace NPMC.Launcher.Core.Rules;

/// <summary>
/// 当前运行环境的抽象，用于 rules 求值。
/// 实测基准（1.20.1 / Windows x64 / Win10）：88 个库中 64 个生效。
/// </summary>
public readonly record struct OsInfo(string Name, string Arch, string Version)
{
    public static OsInfo Windows64(string version = "10.0") => new("windows", "x86_64", version);
}

/// <summary>
/// 启动特性开关。它决定了 arguments 里哪些带 rules 的条目会被放进来——
/// 例如 ${resolution_width}/${resolution_height} 只有在 has_custom_resolution 为真时才出现。
/// </summary>
public sealed class FeatureSet
{
    private readonly Dictionary<string, bool> _flags = new(StringComparer.Ordinal);

    public bool IsEnabled(string name) => _flags.TryGetValue(name, out var value) && value;

    public FeatureSet Enable(string name, bool value = true)
    {
        _flags[name] = value;
        return this;
    }

    public static FeatureSet ForLaunch(bool hasCustomResolution = false) =>
        new FeatureSet().Enable("has_custom_resolution", hasCustomResolution);
}

/// <summary>
/// Minecraft 版本元数据中 rules 的标准求值器。
///
/// 语义（务必别改，这是最容易写错的地方）：
///   1. 没有 rules（null 或空数组）        → 允许
///   2. 有 rules                            → 从 false 开始
///   3. 按数组顺序逐条判断，命中的条目把结果覆盖为 (action == "allow")
///      也就是说后命中的规则优先级更高，而不是 allow 简单地压过 disallow。
/// </summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, OsInfo os, FeatureSet features)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!IsMatch(rule, os, features)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool IsMatch(Rule rule, OsInfo os, FeatureSet features)
    {
        if (rule.Os is { } osRule)
        {
            if (osRule.Name is { Length: > 0 } name &&
                !string.Equals(name, os.Name, StringComparison.OrdinalIgnoreCase))
                return false;

            if (osRule.Arch is { Length: > 0 } arch &&
                !string.Equals(arch, os.Arch, StringComparison.OrdinalIgnoreCase))
                return false;

            // os.version 是正则，不是等值比较（例如 "^10\\." 匹配 "10.0"）
            if (osRule.Version is { Length: > 0 } versionPattern)
            {
                try
                {
                    if (!Regex.IsMatch(os.Version, versionPattern)) return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        if (rule.Features is { Count: > 0 } required)
        {
            foreach (var (feature, expected) in required)
            {
                if (expected && !features.IsEnabled(feature)) return false;
            }
        }

        return true;
    }
}
