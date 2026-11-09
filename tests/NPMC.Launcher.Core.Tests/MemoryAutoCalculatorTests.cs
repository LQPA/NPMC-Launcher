using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class MemoryAutoCalculatorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Known_modpack_recommendation_wins()
    {
        var recommendation = MemoryAutoCalculator.Recommend(400, "All the Mods 10", 64 * Gb, 64 * Gb);

        Assert.Equal(8192, recommendation.MemoryMb);
        Assert.Contains("ATM10", recommendation.Reason);
        Assert.False(recommendation.LimitedByFreeMemory);
    }

    [Fact]
    public void Aliases_are_matched_ignoring_case_and_punctuation()
    {
        var recommendation = MemoryAutoCalculator.Recommend(0, "all-the-mods-10", 64 * Gb, 64 * Gb);

        Assert.Equal(8192, recommendation.MemoryMb);
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(1, 4096)]
    [InlineData(42, 5120)]
    [InlineData(100, 6144)]
    [InlineData(180, 8192)]
    [InlineData(260, 10240)]
    [InlineData(400, 12288)]
    public void Unknown_pack_falls_back_to_mod_count_tiers(int modsCount, int expected)
    {
        var recommendation = MemoryAutoCalculator.Recommend(modsCount, null, 64 * Gb, 64 * Gb);

        Assert.Equal(expected, recommendation.MemoryMb);
    }

    [Fact]
    public void Low_free_memory_lowers_the_recommendation()
    {
        // 16 GB 物理、4 GB 空闲 → 上限 3 GB（75%），ATM10 的 8 GB 会被压下来
        var recommendation = MemoryAutoCalculator.Recommend(400, "ATM10", 16 * Gb, 4 * Gb);

        Assert.Equal(3072, recommendation.MemoryMb);
        Assert.True(recommendation.LimitedByFreeMemory);
        Assert.Contains("空闲内存不足", recommendation.Reason);
    }

    [Fact]
    public void Never_goes_below_the_minimum()
    {
        var recommendation = MemoryAutoCalculator.Recommend(0, null, 8 * Gb, 1 * Gb);

        Assert.Equal(MemoryAutoCalculator.MinimumMb, recommendation.MemoryMb);
    }

    [Fact]
    public void Result_is_always_aligned_to_512_mb()
    {
        for (var freeGb = 2; freeGb <= 32; freeGb += 3)
        {
            var recommendation = MemoryAutoCalculator.Recommend(42, null, 64 * Gb, freeGb * Gb);
            Assert.Equal(0, recommendation.MemoryMb % MemoryAutoCalculator.StepMb);
        }
    }

    [Fact]
    public void Pack_floor_raises_the_auto_value()
    {
        // 纯原版自动值 2048，但整合包预设了 6144 → 用 6144
        var recommendation = MemoryAutoCalculator.Recommend(0, null, 64 * Gb, 64 * Gb, 6144);

        Assert.Equal(6144, recommendation.MemoryMb);
        Assert.Contains("整合包预设", recommendation.Reason);
    }

    [Fact]
    public void Pack_floor_never_lowers_the_auto_value()
    {
        var recommendation = MemoryAutoCalculator.Recommend(400, null, 64 * Gb, 64 * Gb, 2048);

        Assert.True(recommendation.MemoryMb >= 8192);
    }

    [Fact]
    public void Pack_floor_is_still_capped_by_free_memory()
    {
        // 预设 16 GB，但机器只有 4 GB 空闲：不能被预设顶爆
        var recommendation = MemoryAutoCalculator.Recommend(0, null, 16 * Gb, 4 * Gb, 16384);

        Assert.True(recommendation.MemoryMb < 16384);
        Assert.True(recommendation.LimitedByFreeMemory);
    }
}
