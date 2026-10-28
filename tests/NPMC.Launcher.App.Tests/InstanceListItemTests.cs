using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;
using Xunit;

namespace NPMC.Launcher.App.Tests;

public class InstanceListItemTests
{
    [Fact]
    public void Manual_memory_shows_the_configured_value()
    {
        var root = NewRoot();
        try
        {
            var instance = LauncherInstance.Create("test", "test", "1.20.1", IsolationLevel.Version,
                new InstanceSettings { MemoryMb = 6144, AutoMemory = false });

            var item = new InstanceListItem(new LauncherPaths(root), instance);

            Assert.Equal("6144 MB", item.MemoryText);
            Assert.Contains("6144 MB", item.SummaryText);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Auto_memory_summary_reflects_the_recommendation_mode()
    {
        var root = NewRoot();
        try
        {
            var instance = LauncherInstance.Create("auto", "auto", "1.20.1", IsolationLevel.Version,
                new InstanceSettings { MemoryMb = 4096, AutoMemory = true });

            var item = new InstanceListItem(new LauncherPaths(root), instance);

            Assert.StartsWith("自动 ", item.MemoryText);
            Assert.Contains("自动 ", item.SummaryText);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "npmc-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结果
        }
    }
}
