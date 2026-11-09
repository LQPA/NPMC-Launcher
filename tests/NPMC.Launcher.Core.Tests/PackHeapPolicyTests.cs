using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 整合包自带 -Xmx 的取舍：优先保留作者的值，只有物理内存明显不够才退回自动值。
/// </summary>
public class PackHeapPolicyTests
{
    [Fact]
    public void Keeps_pack_heap_when_it_fits()
    {
        // 16GB 机器，整合包要 8G：照用
        var decision = PackHeapPolicy.Evaluate(totalMb: 16384, availableMb: 12000, packHeapMb: 8192, autoHeapMb: 6144);

        Assert.True(decision.KeepPreset);
        Assert.Equal(8192, decision.RecommendedHeapMb);
    }

    [Fact]
    public void Falls_back_when_machine_is_too_small()
    {
        // 8GB 机器装 ATM10（作者要 8G）：不可能，退回自动值
        var decision = PackHeapPolicy.Evaluate(totalMb: 8192, availableMb: 6000, packHeapMb: 8192, autoHeapMb: 4096);

        Assert.False(decision.KeepPreset);
        Assert.Equal(4096, decision.RecommendedHeapMb);
        Assert.Contains("夹紧", decision.Reason);
    }

    [Fact]
    public void Falls_back_when_available_is_lower_than_requested()
    {
        var decision = PackHeapPolicy.Evaluate(totalMb: 32768, availableMb: 4096, packHeapMb: 8192, autoHeapMb: 4096);

        Assert.False(decision.KeepPreset);
        Assert.Equal(4096, decision.RecommendedHeapMb);
        Assert.Contains("可用内存", decision.Reason);
    }

    [Fact]
    public void Borderline_pack_heap_still_allowed()
    {
        // 8GB 机器、整合包要 6G：上限 = min(8192-2048, 8192*0.75) = 6144，刚好放行
        // 可用 7.5G > 6G + 1G 余量，且没超过安全上限 6144
        var decision = PackHeapPolicy.Evaluate(totalMb: 8192, availableMb: 7680, packHeapMb: 6144, autoHeapMb: 4096);

        Assert.True(decision.KeepPreset);
        Assert.Equal(6144, decision.RecommendedHeapMb);
    }

    [Fact]
    public void No_pack_heap_means_auto()
    {
        var decision = PackHeapPolicy.Evaluate(totalMb: 16384, availableMb: 12000, packHeapMb: 0, autoHeapMb: 5120);

        Assert.False(decision.KeepPreset);
        Assert.Equal(5120, decision.RecommendedHeapMb);
    }
}
