using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class LaunchPlanFactoryTests
{
    private static readonly LauncherPaths Paths = new(Path.Combine(Path.GetTempPath(), "npmc-plan"));

    private static Rule AllowOs(string name) =>
        new() { Action = "allow", Os = new RuleOs { Name = name } };

    /// <summary>
    /// 合成一份带全部关键特征的版本元数据：
    /// 一个通用库、一个仅 osx 的库、一个仅 osx 的 JVM 参数、一个 feature 门控的窗口参数。
    /// </summary>
    private static VersionJson BuildVersion() => new()
    {
        Id = "test",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "5",
        AssetIndex = new AssetIndexRef { Id = "5" },
        JavaVersion = new JavaVersionInfo { Component = "java-runtime-gamma", MajorVersion = 17 },
        Libraries = new List<Library>
        {
            new()
            {
                Name = "common",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadArtifact { Path = "c/common/1/common.jar" },
                },
            },
            new()
            {
                Name = "osx-only",
                Rules = new List<Rule> { AllowOs("osx") },
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadArtifact { Path = "o/osx/1/osx.jar" },
                },
            },
        },
        Arguments = new VersionArguments
        {
            Jvm = new List<ArgumentEntry>
            {
                new(null, new[] { "-cp", "${classpath}" }),
                new(new List<Rule> { AllowOs("osx") }, new[] { "-XstartOnFirstThread" }),
            },
            Game = new List<ArgumentEntry>
            {
                new(null, new[] { "--version", "${version_name}" }),
                new(
                    new List<Rule>
                    {
                        new Rule
                        {
                            Action = "allow",
                            Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true },
                        },
                    },
                    new[] { "--width", "${resolution_width}", "--height", "${resolution_height}" }),
            },
        },
    };

    [Fact]
    public void Filters_out_platform_specific_libraries_and_arguments()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "test", BuildVersion(), LaunchContext.OfflineProbe(), OsInfo.Windows64(), memoryMb: 2048);

        Assert.Contains("-Xmx2048M", plan.JvmArguments);
        Assert.Contains("-Xms1024M", plan.JvmArguments);
        Assert.DoesNotContain("-XstartOnFirstThread", plan.JvmArguments);
        Assert.DoesNotContain(plan.JvmArguments, a => a.EndsWith("osx.jar", StringComparison.Ordinal));
        Assert.Contains("--width", plan.GameArguments);
        Assert.Contains("1280", plan.GameArguments);
    }

    [Fact]
    public void Java_path_follows_java_version_component()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "test", BuildVersion(), LaunchContext.OfflineProbe(), OsInfo.Windows64());

        Assert.Equal(Paths.JavaExe("java-runtime-gamma"), plan.JavaPath);
    }

    [Fact]
    public void Classpath_contains_client_jar_plus_effective_libraries_only()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "test", BuildVersion(), LaunchContext.OfflineProbe(), OsInfo.Windows64());

        var jvm = plan.JvmArguments.ToList();
        var classpath = jvm[jvm.IndexOf("-cp") + 1];

        // client.jar + common.jar；osx.jar 必须被 rules 排除
        Assert.Equal(2, classpath.Split(';').Length);
        Assert.Contains("common.jar", classpath);
        Assert.DoesNotContain("osx.jar", classpath);
    }

    [Fact]
    public void No_unresolved_placeholders_after_expansion()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "test", BuildVersion(), LaunchContext.OfflineProbe(), OsInfo.Windows64());

        Assert.Empty(LaunchPlanFactory.FindUnresolvedPlaceholders(plan));
    }

    [Fact]
    public void Window_arguments_disappear_when_feature_disabled()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "test", BuildVersion(), LaunchContext.OfflineProbe(), OsInfo.Windows64(),
            hasCustomResolution: false);

        Assert.DoesNotContain("--width", plan.GameArguments);
        Assert.DoesNotContain("--height", plan.GameArguments);
    }

    [Fact]
    public void Legacy_version_without_arguments_throws_clearly()
    {
        var legacy = BuildVersion() with { Arguments = null };
        var ex = Assert.Throws<NotSupportedException>(() =>
            LaunchPlanFactory.Create(Paths, "test", legacy, LaunchContext.OfflineProbe(), OsInfo.Windows64()));

        Assert.Contains("minecraftArguments", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_placeholder_is_detected()
    {
        var version = BuildVersion();
        var game = version.Arguments!.Game.ToList();
        game.Add(new ArgumentEntry(null, new[] { "${whoops}" }));
        version = version with
        {
            Arguments = new VersionArguments { Jvm = version.Arguments.Jvm, Game = game },
        };

        var plan = LaunchPlanFactory.Create(
            Paths, "test", version, LaunchContext.OfflineProbe(), OsInfo.Windows64());

        Assert.Contains("${whoops}", LaunchPlanFactory.FindUnresolvedPlaceholders(plan));
    }

    [Fact]
    public void Display_string_quotes_paths_with_spaces_and_semicolons()
    {
        var plan = new LaunchPlan("/jdk/bin/java.exe", "/work", "Main", new[] { "-cp", "a.jar;b.jar" }, new[] { "--x" });
        var text = plan.ToDisplayString();

        Assert.Contains("\"a.jar;b.jar\"", text);
        Assert.Contains("/jdk/bin/java.exe", text);
        Assert.Contains("--x", text);
    }
}

/// <summary>
/// 针对真实 1.20.1 元数据的回归测试。
///
/// 该元数据文件位于 .data/（本机运行数据，不入库），所以文件不存在时测试直接返回。
/// 一旦存在，就用它钉住我们实测过的基准数字：
///   88 个库 → Windows x64 生效 64 个 → 游戏参数 26 个，且无残留占位符。
///
/// 注意 classpath 条目数（51）比"生效库数 + 1"少：1.20.1 的元数据给每个 LWJGL 模块都列了
/// natives-windows / -arm64 / -x86 三个变体，且三条规则都只按操作系统过滤。按官方行为，
/// 每个模块只留与当前架构匹配的那一个（见 <see cref="NPMC.Launcher.Core.Rules.NativeVariants"/>），
/// 所以 64 个生效库里有 14 个是多余的架构变体，最终 classpath = 50 个 jar + client.jar。
/// 旧基准 65 是"三种架构全塞进去"的数字，LWJGL 恰好也能跑，但那不是正确行为。
/// </summary>
public class RealVersionMetadataTests
{
    [Fact]
    public void Real_1_20_1_metadata_matches_verified_baseline()
    {
        var root = FindRepoRoot();
        if (root is null) return;

        var jsonPath = Path.Combine(root, ".data", "versions", "1.20.1", "1.20.1.json");
        if (!File.Exists(jsonPath)) return;

        var version = VersionJsonSerializer.Parse(File.ReadAllText(jsonPath));
        var os = OsInfo.Windows64();
        var features = FeatureSet.ForLaunch(hasCustomResolution: true);

        Assert.Equal(88, version.Libraries.Count);

        var effective = version.Libraries
            .Where(lib => RuleEvaluator.IsAllowed(lib.Rules, os, features))
            .ToList();

        Assert.Equal(64, effective.Count);

        // 原版不应出现这两种情况；一旦出现说明元数据形态变了，实现要跟着改
        Assert.Empty(effective.Where(lib => lib.Downloads?.Artifact is null));
        Assert.Empty(version.Libraries.Where(lib => lib.Natives is { Count: > 0 }));

        var paths = new LauncherPaths(root);
        var plan = LaunchPlanFactory.Create(
            paths, "1.20.1", version, LaunchContext.OfflineProbe(), os, memoryMb: 4096);

        Assert.Equal(26, plan.GameArguments.Count);
        Assert.Empty(LaunchPlanFactory.FindUnresolvedPlaceholders(plan));

        // classpath 条目 = 50 个库 + client.jar（每模块只留一个架构变体，见类型注释）
        Assert.Equal(51, plan.JvmArguments.Last().Split(';').Length);
    }

    /// <summary>从测试程序集目录向上找含解决方案文件的目录。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NPMC.Launcher.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
