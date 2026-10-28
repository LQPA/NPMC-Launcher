using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class ModpackInstallMemoryTests
{
    [Fact]
    public void Recommendation_overrides_default_and_turns_off_auto()
    {
        var defaults = new InstanceSettings { MemoryMb = 4096, AutoMemory = true };

        var applied = ModpackInstallMemory.Apply(defaults, 10112);

        Assert.Equal(10112, applied.MemoryMb);
        Assert.False(applied.AutoMemory);
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
