using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 版本通道的归一化。
///
/// 这组测试存在的理由：界面上"这个版本是正式版还是 alpha"靠的就是这里，
/// 而 alpha 版本并不常见 —— 不可能靠"找一个恰好发过 alpha 的项目"来保证正确。
/// </summary>
public class ReleaseChannelTests
{
    [Theory]
    [InlineData("release", ReleaseChannel.Release)]
    [InlineData("beta", ReleaseChannel.Beta)]
    [InlineData("alpha", ReleaseChannel.Alpha)]
    [InlineData("ALPHA", ReleaseChannel.Alpha)]
    [InlineData(" Beta ", ReleaseChannel.Beta)]
    [InlineData("", ReleaseChannel.Release)]
    [InlineData(null, ReleaseChannel.Release)]
    [InlineData("something-else", ReleaseChannel.Release)]
    public void Modrinth_types_map_to_channels(string? versionType, ReleaseChannel expected)
    {
        Assert.Equal(expected, ReleaseChannelExtensions.FromModrinthType(versionType));
    }

    [Theory]
    [InlineData(1, ReleaseChannel.Release)]
    [InlineData(2, ReleaseChannel.Beta)]
    [InlineData(3, ReleaseChannel.Alpha)]
    [InlineData(0, ReleaseChannel.Release)]
    [InlineData(99, ReleaseChannel.Release)]
    public void CurseForge_release_types_map_to_channels(int releaseType, ReleaseChannel expected)
    {
        Assert.Equal(expected, ReleaseChannelExtensions.FromCurseForgeReleaseType(releaseType));
    }

    [Theory]
    [InlineData(ReleaseChannel.Release, "R", false)]
    [InlineData(ReleaseChannel.Beta, "B", true)]
    [InlineData(ReleaseChannel.Alpha, "A", true)]
    public void Badge_letter_and_prerelease_flag(ReleaseChannel channel, string badge, bool isPrerelease)
    {
        Assert.Equal(badge, channel.Badge());
        Assert.Equal(isPrerelease, channel.IsPrerelease());
    }

    [Fact]
    public void Every_channel_has_a_display_name()
    {
        foreach (var channel in Enum.GetValues<ReleaseChannel>())
            Assert.False(string.IsNullOrWhiteSpace(channel.DisplayName()));
    }
}
