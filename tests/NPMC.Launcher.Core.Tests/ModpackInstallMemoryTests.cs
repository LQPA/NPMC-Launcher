using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class ModpackInstallMemoryTests
{
    [Fact]
    public void Recommendation_keeps_auto_and_records_a_floor()
    {
        var defaults = new InstanceSettings { MemoryMb = 4096, AutoMemory = true };

        var applied = ModpackInstallMemory.Apply(defaults, 10112);

        // 用户选了自动内存 → 保持自动，作者推荐值退化为"自动的下限"
        Assert.True(applied.AutoMemory);
        Assert.Equal(10112, applied.MemoryMb);
        Assert.Equal(10112, applied.MemoryFloorMb);
    }

    [Fact]
    public void Recommendation_overrides_manual_memory()
    {
        var defaults = new InstanceSettings { MemoryMb = 4096, AutoMemory = false };

        var applied = ModpackInstallMemory.Apply(defaults, 8192);

        Assert.False(applied.AutoMemory);
        Assert.Equal(8192, applied.MemoryMb);
        Assert.Equal(0, applied.MemoryFloorMb);
    }

    [Fact]
    public void Missing_recommendation_keeps_defaults()
    {
        var defaults = new InstanceSettings { MemoryMb = 4096, AutoMemory = true };

        Assert.Same(defaults, ModpackInstallMemory.Apply(defaults, 0));
    }

    [Fact]
    public void Recommendation_is_clamped_to_a_sane_range()
    {
        Assert.Equal(ModpackInstallMemory.MinimumMb,
            ModpackInstallMemory.Apply(new InstanceSettings(), 128).MemoryMb);

        Assert.Equal(ModpackInstallMemory.MaximumMb,
            ModpackInstallMemory.Apply(new InstanceSettings(), 999999).MemoryMb);
    }
}
