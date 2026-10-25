using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Rules;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// rules 求值是全项目最容易写错、也最难排查的地方：
/// 写错不会报错，只会表现为"少一个库"或"少一个参数"，最后变成启动失败。
/// 所以这里把每一条语义都钉死。
/// </summary>
public class RuleEvaluatorTests
{
    private static readonly OsInfo WindowsX64 = OsInfo.Windows64();          // windows / x86_64 / 10.0
    private static readonly OsInfo WindowsX86 = new("windows", "x86", "10.0");
    private static readonly OsInfo Windows7 = new("windows", "x86_64", "6.1");

    private static FeatureSet NoFeatures => FeatureSet.ForLaunch(hasCustomResolution: false);
    private static FeatureSet WithResolution => FeatureSet.ForLaunch(hasCustomResolution: true);

    private static Rule Allow(
        string? osName = null, string? osArch = null, string? osVersion = null,
        Dictionary<string, bool>? features = null)
    {
        var hasOs = osName is not null || osArch is not null || osVersion is not null;
        return new Rule
        {
            Action = "allow",
            Os = hasOs ? new RuleOs { Name = osName, Arch = osArch, Version = osVersion } : null,
            Features = features,
        };
    }

    private static Rule Deny(
        string? osName = null, string? osArch = null, string? osVersion = null,
        Dictionary<string, bool>? features = null)
        => Allow(osName, osArch, osVersion, features) with { Action = "disallow" };

    // ---------------------------------------------------------------- 基础

    [Fact]
    public void Null_rules_means_allowed()
        => Assert.True(RuleEvaluator.IsAllowed(null, WindowsX64, NoFeatures));

    [Fact]
    public void Empty_rules_means_allowed()
        => Assert.True(RuleEvaluator.IsAllowed(Array.Empty<Rule>(), WindowsX64, NoFeatures));

    [Fact]
    public void Allow_rule_matching_os_is_allowed()
        => Assert.True(RuleEvaluator.IsAllowed(new[] { Allow("windows") }, WindowsX64, NoFeatures));

    [Fact]
    public void Allow_rule_for_other_os_is_denied()
    {
        // 1.20.1 里 ca.weblite:java-objc-bridge 就是这条规则，Windows 上必须被排除
        Assert.False(RuleEvaluator.IsAllowed(new[] { Allow("osx") }, WindowsX64, NoFeatures));
        Assert.False(RuleEvaluator.IsAllowed(new[] { Allow("linux") }, WindowsX64, NoFeatures));
    }

    [Fact]
    public void Disallow_rule_matching_os_is_denied()
        => Assert.False(RuleEvaluator.IsAllowed(new[] { Deny("windows") }, WindowsX64, NoFeatures));

    [Fact]
    public void Disallow_rule_for_other_os_leaves_it_allowed()
    {
        var rules = new[] { Allow("windows"), Deny("osx") };
        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
    }

    // ------------------------------------------------- 顺序语义（最容易写错）

    [Fact]
    public void Later_matching_rule_wins_allow_then_disallow()
    {
        // 不是"allow 永远压过 disallow"，而是按顺序覆盖
        var rules = new[] { Allow("windows"), Deny("windows") };
        Assert.False(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
    }

    [Fact]
    public void Later_matching_rule_wins_disallow_then_allow()
    {
        var rules = new[] { Deny("windows"), Allow("windows") };
        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
    }

    [Fact]
    public void Non_matching_rules_do_not_change_the_result()
    {
        var rules = new[] { Deny("osx"), Deny("linux") };
        // 一条都没命中 → 初始值 false
        Assert.False(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
    }

    // ---------------------------------------------------------------- arch

    [Fact]
    public void Arch_mismatch_is_denied()
    {
        // 1.20.1 的 -Xss1M 就挂在 os.arch = x86 上，x64 必须排除
        Assert.False(RuleEvaluator.IsAllowed(new[] { Allow(osArch: "x86") }, WindowsX64, NoFeatures));
    }

    [Fact]
    public void Arch_match_is_allowed()
        => Assert.True(RuleEvaluator.IsAllowed(new[] { Allow(osArch: "x86_64") }, WindowsX64, NoFeatures));

    [Fact]
    public void Arch_rule_matches_x86_host()
        => Assert.True(RuleEvaluator.IsAllowed(new[] { Allow(osArch: "x86") }, WindowsX86, NoFeatures));

    // ------------------------------------------------------ os.version 正则

    [Fact]
    public void Os_version_is_a_regex_not_equality()
    {
        // ^10\. 应匹配 "10.0"
        var rules = new[] { Allow(osVersion: "^10\\.") };
        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows7, NoFeatures));
    }

    [Fact]
    public void Invalid_os_version_regex_is_treated_as_no_match()
    {
        var rules = new[] { Allow(osVersion: "^(unclosed") };
        Assert.False(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
    }

    // ------------------------------------------------------------- features

    [Fact]
    public void Feature_gated_rule_is_denied_when_feature_is_off()
    {
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = true };
        var rules = new[] { Allow(features: features) };
        Assert.False(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
    }

    [Fact]
    public void Feature_gated_rule_is_allowed_when_feature_is_on()
    {
        // 这条规则决定了 --width / --height 会不会出现在游戏参数里
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = true };
        var rules = new[] { Allow(features: features) };
        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX64, WithResolution));
    }

    [Fact]
    public void Multiple_features_all_must_be_enabled()
    {
        var features = new Dictionary<string, bool>
        {
            ["has_custom_resolution"] = true,
            ["is_demo_user"] = true,
        };
        var rules = new[] { Allow(features: features) };
        Assert.False(RuleEvaluator.IsAllowed(rules, WindowsX64, WithResolution));
    }

    // ------------------------------------------------------------ 组合场景

    [Fact]
    public void Combined_os_and_feature_rules()
    {
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = true };
        var rules = new[] { Allow(osName: "windows", features: features) };

        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX64, WithResolution));
        Assert.False(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
        Assert.False(RuleEvaluator.IsAllowed(rules, new OsInfo("linux", "x86_64", "10.0"), WithResolution));
    }

    [Fact]
    public void Lwjgl_natives_windows_rule_shape()
    {
        // org.lwjgl:lwjgl:3.3.1:natives-windows 的真实规则：只约束 os.name，
        // 不约束 arch —— 这解释了为什么 Windows x64 上会额外下载 arm64 / x86 变体。
        var rules = new[] { Allow("windows") };
        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX64, NoFeatures));
        Assert.True(RuleEvaluator.IsAllowed(rules, new OsInfo("windows", "arm64", "10.0"), NoFeatures));
        Assert.True(RuleEvaluator.IsAllowed(rules, WindowsX86, NoFeatures));
        Assert.False(RuleEvaluator.IsAllowed(rules, new OsInfo("osx", "x86_64", "10.0"), NoFeatures));
    }
}
