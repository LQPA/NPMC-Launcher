using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 旧版 minecraftArguments（1.13 之前）的组装。
///
/// 这类版本没有 arguments 结构，而是"一整串空格分隔的参数"，
/// 并且**不包含** -cp 与 -Djava.library.path —— 这两项必须自己补，
/// 漏了就是启动时找不到主类。
/// </summary>
public class LegacyLaunchArgumentsTests
{
    private static readonly LauncherPaths Paths = new(Path.Combine(Path.GetTempPath(), "npmc-legacy"));

    private static VersionJson LegacyVersion() => new()
    {
        Id = "1.7.10-Forge10.13.4.1614-1.7.10",
        Type = "release",
        MainClass = "net.minecraft.launchwrapper.Launch",
        Assets = "1.7.10",
        AssetIndex = new AssetIndexRef { Id = "1.7.10" },
        JavaVersion = new JavaVersionInfo { Component = "jre-legacy", MajorVersion = 8 },
        MinecraftArguments =
            "--username ${auth_player_name} " +
            "--gameDir ${game_directory} " +
            "--version ${version_name} " +
            "--assetsDir ${assets_root} " +
            "--assetIndex ${assets_index_name} " +
            "--userType ${user_type} " +
            "--userProperties ${user_properties} " +
            "--tweakClass cpw.mods.fml.common.launcher.FMLTweaker",
        Libraries = new List<Library>
        {
            new()
            {
                Name = "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10",
                Url = "https://maven.minecraftforge.net/",
            },
            new()
            {
                Name = "net.minecraft:launchwrapper:1.12",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadArtifact { Path = "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar" },
                },
            },
        },
    };

    [Fact]
    public void Legacy_arguments_are_split_and_expanded()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "1.7.10-Forge10.13.4.1614-1.7.10", LegacyVersion(), LaunchContext.OfflineProbe(),
            OsInfo.Windows64(), gameDirectory: @"C:\games\forge");

        // 占位符必须全部替换掉
        Assert.DoesNotContain(plan.GameArguments, argument => argument.Contains("${"));

        Assert.Contains("--username", plan.GameArguments);
        Assert.Contains("Probe", plan.GameArguments);
        Assert.Contains(@"C:\games\forge", plan.GameArguments);
        Assert.Contains("1.7.10", plan.GameArguments);
        Assert.Contains("legacy", plan.GameArguments);   // ${user_type}
    }

    [Fact]
    public void User_properties_is_expanded_to_valid_json()
    {
        // 1.7.10 的 Main 会把这个值当 JSON 解析：
        // 留成字面量 `${user_properties}` 就会报 "Expected BEGIN_OBJECT but was STRING"
        var plan = LaunchPlanFactory.Create(
            Paths, "1.7.10-Forge10.13.4.1614-1.7.10", LegacyVersion(), LaunchContext.OfflineProbe(),
            OsInfo.Windows64());

        var index = plan.GameArguments.ToList().IndexOf("--userProperties");
        Assert.True(index >= 0, "旧版参数里应当包含 --userProperties");
        Assert.Equal("{}", plan.GameArguments[index + 1]);
    }

    [Fact]
    public void Legacy_arguments_get_java_library_path_and_classpath()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "1.7.10-Forge10.13.4.1614-1.7.10", LegacyVersion(), LaunchContext.OfflineProbe(),
            OsInfo.Windows64());

        Assert.Contains(plan.JvmArguments, argument => argument.StartsWith("-Djava.library.path="));

        var cpIndex = plan.JvmArguments.ToList().IndexOf("-cp");
        Assert.True(cpIndex >= 0, "旧版版本必须自己补 -cp");
        Assert.True(cpIndex + 1 < plan.JvmArguments.Count, "-cp 后面必须跟 classpath");

        var classpath = plan.JvmArguments[cpIndex + 1];
        Assert.Contains("launchwrapper-1.12.jar", classpath);
        Assert.Contains("1.7.10-Forge10.13.4.1614-1.7.10", classpath.Split(';')[^1]);   // client jar 在最后
    }

    [Fact]
    public void Legacy_version_uses_its_declared_java_component()
    {
        var plan = LaunchPlanFactory.Create(
            Paths, "1.7.10-Forge10.13.4.1614-1.7.10", LegacyVersion(), LaunchContext.OfflineProbe(),
            OsInfo.Windows64());

        Assert.Equal(Paths.JavaExe("jre-legacy"), plan.JavaPath);
    }

    [Fact]
    public void Version_without_any_arguments_is_rejected_with_a_clear_message()
    {
        var broken = new VersionJson { Id = "broken", MainClass = "x", Libraries = new List<Library>() };

        var error = Assert.Throws<NotSupportedException>(() => LaunchPlanFactory.Create(
            Paths, "broken", broken, LaunchContext.OfflineProbe(), OsInfo.Windows64()));

        Assert.Contains("既没有 arguments 也没有 minecraftArguments", error.Message);
    }

    [Theory]
    [InlineData("--username ${auth_player_name} --version ${version_name}", "--username", "Probe")]
    [InlineData("--userType ${user_type}", "--userType", "legacy")]
    public void Splitting_happens_before_expansion(string arguments, string expectedFlag, string expectedValue)
    {
        var builder = ArgumentBuilder.CreateDefault(
            @"C:\games", @"C:\assets", @"C:\natives", "test",
            new VersionJson { Type = "release" }, LaunchContext.OfflineProbe(), new[] { "a.jar" });

        var expanded = LaunchPlanFactory.ExpandLegacyArguments(builder, arguments);

        Assert.Equal(expectedFlag, expanded[0]);
        Assert.Equal(expectedValue, expanded[1]);
    }
}
