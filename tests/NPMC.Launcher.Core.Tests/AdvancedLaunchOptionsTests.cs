using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Launch;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 概览页「高级选项」对启动计划的影响：
/// 自定义 JVM / 游戏参数必须真的进到命令行，否则界面只是摆设。
/// </summary>
public class AdvancedLaunchOptionsTests
{
    private static VersionJson Version() => new()
    {
        Id = "1.21.1",
        MainClass = "net.minecraft.client.main.Main",
        AssetIndex = new AssetIndexRef { Id = "1.21.1" },
        Libraries = new List<Library>(),
        Arguments = new VersionArguments
        {
            Game = new List<ArgumentEntry> { new(null, new[] { "--username", "${auth_player_name}" }) },
            Jvm = new List<ArgumentEntry>(),
        },
    };

    [Fact]
    public void Extra_arguments_land_in_the_launch_plan()
    {
        var root = Path.Combine(Path.GetTempPath(), "npmc-advanced-" + Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new LauncherPaths(root);
            var instance = LauncherInstance.Create("adv", "adv", "1.21.1", IsolationLevel.Version,
                new InstanceSettings
                {
                    ExtraJvmArguments = new List<string> { "-XX:+UseG1GC" },
                    ExtraGameArguments = new List<string> { "--demo", "--width", "1600" },
                    CustomTitle = "我的世界",
                });

            var prepared = GameLauncher.Prepare(paths, instance, Version(), new LauncherSettings());

            Assert.Contains("-XX:+UseG1GC", prepared.Plan.JvmArguments);
            Assert.Contains("--demo", prepared.Plan.GameArguments);
            Assert.Equal("1600", prepared.Plan.GameArguments[^1]);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
