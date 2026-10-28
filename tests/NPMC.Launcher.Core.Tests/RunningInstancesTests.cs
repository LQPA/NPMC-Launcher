using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class RunningInstancesTests
{
    [Fact]
    public void Same_instance_can_only_be_registered_once()
    {
        var running = new RunningInstances();

        Assert.True(running.TryAdd("a"));
        Assert.False(running.TryAdd("a"));
        Assert.Equal(1, running.Count);
        Assert.True(running.Contains("a"));
    }

    [Fact]
    public void Different_instances_can_run_at_the_same_time()
    {
        var running = new RunningInstances();

        Assert.True(running.TryAdd("a"));
        Assert.True(running.TryAdd("b"));
        Assert.Equal(2, running.Count);
        Assert.Equal(new[] { "a", "b" }, running.Snapshot().OrderBy(id => id));
    }

    [Fact]
    public void Removing_one_instance_leaves_the_others_running()
    {
        var running = new RunningInstances();
        running.TryAdd("a");
        running.TryAdd("b");

        running.Remove("a");

        Assert.False(running.Contains("a"));
        Assert.True(running.Contains("b"));
        Assert.Equal(1, running.Count);

        // 关闭后可以重新启动同一个实例
        Assert.True(running.TryAdd("a"));
    }
}
