using NPMC.Launcher.Infrastructure.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 整合包安装进度换算。进度条直接吃这个百分比，
/// 算错会让进度条停在 0 或 100 不动 —— 这类"看起来在跑其实没动"的错误很难被发现。
/// </summary>
public class ModpackProgressTests
{
    [Theory]
    [InlineData(0, 10, 0d)]
    [InlineData(1, 10, 10d)]
    [InlineData(5, 10, 50d)]
    [InlineData(10, 10, 100d)]
    public void Percent_follows_completed_over_total(int completed, int total, double expected)
    {
        var progress = new ModpackInstallProgress(completed, total, "mods/a.jar");

        Assert.Equal(expected, progress.Percent);
    }

    [Fact]
    public void Percent_is_unknown_when_total_is_zero()
    {
        var progress = new ModpackInstallProgress(0, 0, "");

        // 没有总数时进度条应该退回"滚动"而不是显示 0%
        Assert.Null(progress.Percent);
    }

    [Fact]
    public void Path_is_kept_for_the_status_line()
    {
        var progress = new ModpackInstallProgress(3, 7, "resourcepacks/pack.zip");

        Assert.Equal("resourcepacks/pack.zip", progress.Path);
    }
}
