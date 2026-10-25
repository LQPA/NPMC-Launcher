using System.Text.Json;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Rules;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// arguments 里的元素既可能是纯字符串，也可能是 { rules, value } 对象。
/// 这个多态是自定义转换器处理的，必须单独覆盖。
/// </summary>
public class ArgumentEntryConverterTests
{
    private static readonly JsonSerializerOptions Options = new();

    [Fact]
    public void Plain_string_becomes_single_value_without_rules()
    {
        var entry = JsonSerializer.Deserialize<ArgumentEntry>("\"--demo\"", Options)!;
        Assert.Null(entry.Rules);
        Assert.Equal(new[] { "--demo" }, entry.Values);
    }

    [Fact]
    public void Object_with_string_value_and_feature_rule()
    {
        const string json = """
            {"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"}
            """;

        var entry = JsonSerializer.Deserialize<ArgumentEntry>(json, Options)!;

        Assert.NotNull(entry.Rules);
        Assert.Single(entry.Rules!);
        Assert.Equal("allow", entry.Rules![0].Action);
        Assert.True(entry.Rules[0].Features!["is_demo_user"]);
        Assert.Equal(new[] { "--demo" }, entry.Values);
    }

    [Fact]
    public void Object_with_array_value()
    {
        const string json = """
            {"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],
             "value":["--width","${resolution_width}","--height","${resolution_height}"]}
            """;

        var entry = JsonSerializer.Deserialize<ArgumentEntry>(json, Options)!;

        Assert.Equal(4, entry.Values.Count);
        Assert.Equal("--width", entry.Values[0]);
        Assert.Equal("${resolution_width}", entry.Values[1]);
        Assert.Equal("${resolution_height}", entry.Values[3]);
    }

    [Fact]
    public void Object_with_os_rule()
    {
        const string json = """
            {"rules":[{"action":"allow","os":{"name":"windows"}}],"value":"-XX:HeapDumpPath=x"}
            """;

        var entry = JsonSerializer.Deserialize<ArgumentEntry>(json, Options)!;

        Assert.Equal("windows", entry.Rules![0].Os!.Name);
        Assert.Equal("-XX:HeapDumpPath=x", entry.Values[0]);
    }

    [Fact]
    public void Object_without_rules_or_value_yields_empty()
    {
        var entry = JsonSerializer.Deserialize<ArgumentEntry>("{}", Options)!;
        Assert.Null(entry.Rules);
        Assert.Empty(entry.Values);
    }
}

/// <summary>
/// 参数展开与 rules 过滤。这里锁死"哪些参数会出现在最终命令行里"。
/// </summary>
public class ArgumentBuilderTests
{
    private static readonly OsInfo Windows = OsInfo.Windows64();
    private static readonly FeatureSet NoFeatures = FeatureSet.ForLaunch(hasCustomResolution: false);
    private static readonly FeatureSet WithResolution = FeatureSet.ForLaunch(hasCustomResolution: true);

    private static ArgumentEntry Plain(params string[] values) => new(null, values);

    private static ArgumentEntry Gated(IReadOnlyList<Rule> rules, params string[] values) => new(rules, values);

    private static List<Rule> AllowOs(string name) =>
        new() { new Rule { Action = "allow", Os = new RuleOs { Name = name } } };

    private static List<Rule> RequireFeature(string feature) =>
        new()
        {
            new Rule
            {
                Action = "allow",
                Features = new Dictionary<string, bool> { [feature] = true },
            },
        };

    [Fact]
    public void Expands_placeholder_inside_value()
    {
        var builder = new ArgumentBuilder().Set("${version_name}", "1.20.1");
        var result = builder.Build(new[] { Plain("--version", "${version_name}") }, Windows, NoFeatures);
        Assert.Equal(new[] { "--version", "1.20.1" }, result);
    }

    [Fact]
    public void Expands_multiple_placeholders_in_one_value()
    {
        var builder = new ArgumentBuilder()
            .Set("${a}", "A")
            .Set("${b}", "B");

        var result = builder.Build(new[] { Plain("${a}-${b}") }, Windows, NoFeatures);
        Assert.Equal(new[] { "A-B" }, result);
    }

    [Fact]
    public void Skips_entries_whose_rules_do_not_match()
    {
        var result = new ArgumentBuilder().Build(
            new[] { Plain("--keep"), Gated(AllowOs("osx"), "-XstartOnFirstThread") },
            Windows, NoFeatures);

        Assert.Equal(new[] { "--keep" }, result);
    }

    [Fact]
    public void Includes_feature_gated_entries_only_when_feature_enabled()
    {
        var builder = new ArgumentBuilder()
            .Set("${resolution_width}", "1280")
            .Set("${resolution_height}", "720");

        var entries = new[]
        {
            Gated(RequireFeature("has_custom_resolution"),
                "--width", "${resolution_width}", "--height", "${resolution_height}"),
        };

        Assert.Empty(builder.Build(entries, Windows, NoFeatures));
        Assert.Equal(new[] { "--width", "1280", "--height", "720" },
            builder.Build(entries, Windows, WithResolution));
    }

    [Fact]
    public void Unknown_placeholder_is_left_untouched()
    {
        // 故意不替换未知占位符，交给 FindUnresolvedPlaceholders 统一检查
        var result = new ArgumentBuilder().Build(new[] { Plain("${unknown_thing}") }, Windows, NoFeatures);
        Assert.Equal(new[] { "${unknown_thing}" }, result);
    }

    [Fact]
    public void Empty_entry_list_gives_empty_result()
        => Assert.Empty(new ArgumentBuilder().Build(Array.Empty<ArgumentEntry>(), Windows, NoFeatures));

    [Fact]
    public void Classpath_placeholder_joins_with_semicolon()
    {
        var builder = new ArgumentBuilder().Set("${classpath}", "a.jar;b.jar");
        var result = builder.Build(new[] { Plain("-cp", "${classpath}") }, Windows, NoFeatures);
        Assert.Equal(new[] { "-cp", "a.jar;b.jar" }, result);
    }
}
