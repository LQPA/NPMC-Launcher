using NPMC.Launcher.Core.Models;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 愚人节版本识别与元数据。清单里没有类型字段可用，
/// 所以版本号清单、年份/主题、日期兜底这三条都要有测试盯着。
/// </summary>
public class AprilFoolsVersionsTests
{
    [Theory]
    [InlineData("15w14a", 2015, "爱之石")]
    [InlineData("1.RV-Pre1", 2016, "现实更新")]
    [InlineData("3D Shareware v1.34", 2019, "3D 共享软件")]
    [InlineData("20w14infinite", 2020, "无限维度")]
    [InlineData("22w13oneblockatatime", 2022, "一个方块")]
    [InlineData("23w13a_or_b", 2023, "投票更新")]
    [InlineData("24w14potato", 2024, "毒马铃薯")]
    [InlineData("25w14craftmine", 2025, "合成地雷")]
    public void Known_versions_carry_year_and_theme(string id, int year, string theme)
    {
        var release = AprilFoolsVersions.Find(id);

        Assert.NotNull(release);
        Assert.Equal(year, release!.Year);
        Assert.Equal(theme, release.Theme);
        Assert.Contains(year.ToString(), release.ThemeText);
        Assert.True(AprilFoolsVersions.IsAprilFools(id));
    }

    [Theory]
    [InlineData("1.20.1")]
    [InlineData("23w13a")]          // 普通快照，不是 _or_b
    [InlineData("1.16.5-rc1")]      // 候选版本，不是愚人节
    [InlineData("b1.7.3")]
    [InlineData("")]
    public void Normal_versions_are_not_april_fools(string id)
    {
        Assert.False(AprilFoolsVersions.IsAprilFools(id));
        Assert.Null(AprilFoolsVersions.Find(id));
    }

    [Fact]
    public void Unknown_snapshot_released_on_april_first_is_treated_as_april_fools()
    {
        var aprilFirst = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(AprilFoolsVersions.IsAprilFools("26w14something", aprilFirst, "snapshot"));

        // 兜底判定没有年份与主题可显示
        Assert.Null(AprilFoolsVersions.Find("26w14something"));
    }

    [Fact]
    public void Release_published_on_april_first_is_not_april_fools()
    {
        // 26.1.1 就是 4 月 1 日发布的正式版；按日期判断会把它误归为愚人节
        var aprilFirst = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.False(AprilFoolsVersions.IsAprilFools("26.1.1", aprilFirst, "release"));
    }

    [Fact]
    public void Date_heuristic_requires_explicit_snapshot_type()
    {
        var aprilFirst = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.False(AprilFoolsVersions.IsAprilFools("26w14something", aprilFirst));
    }

    [Fact]
    public void Date_heuristic_does_not_fire_on_other_days()
    {
        var march31 = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.False(AprilFoolsVersions.IsAprilFools("26w13a", march31, "snapshot"));
    }

    [Fact]
    public void Known_ids_take_precedence_over_date()
    {
        // 1.RV-Pre1 是 3 月 31 日发布的，靠清单而不是日期识别
        Assert.True(AprilFoolsVersions.IsAprilFools("1.RV-Pre1", new DateTimeOffset(2016, 3, 31, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Known_list_covers_2015_through_2025()
    {
        Assert.Equal(8, AprilFoolsVersions.Known.Count);
        Assert.All(AprilFoolsVersions.Known, release => Assert.InRange(release.Year, 2015, 2025));
    }
}
