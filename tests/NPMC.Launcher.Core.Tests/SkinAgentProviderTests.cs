using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Infrastructure.Skins;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class SkinAgentProviderTests
{
    [Theory]
    [InlineData("4.0.43", true)]
    [InlineData("6.0.58", true)]
    [InlineData("7.0.63", true)]
    [InlineData("10.0.77", false)]
    [InlineData("1.5.21", false)]
    public void Session_host_override_is_only_claimed_for_supported_authlib_versions(
        string authlibVersion, bool expected)
    {
        var version = VersionJsonSerializer.Parse(
            $"{{\"id\":\"test\",\"libraries\":[{{\"name\":\"com.mojang:authlib:{authlibVersion}\"}}]}}");

        Assert.Equal(expected, SkinAgentProvider.SupportsSessionHostOverride(version));
    }

    [Theory]
    [InlineData("1.5.21", true)]
    [InlineData("3.0.0", true)]
    [InlineData("7.0.63", true)]
    [InlineData("10.0.77", false)]
    public void Agent_attachment_covers_authlib_1_to_7(string authlibVersion, bool expected)
    {
        var version = VersionJsonSerializer.Parse(
            $"{{\"id\":\"test\",\"libraries\":[{{\"name\":\"com.mojang:authlib:{authlibVersion}\"}}]}}");

        Assert.Equal(expected, SkinAgentProvider.SupportsSkinAgent(version));
    }

    [Fact]
    public void No_authlib_library_means_no_skin_agent()
    {
        var version = VersionJsonSerializer.Parse(
            "{\"id\":\"test\",\"libraries\":[{\"name\":\"com.google.guava:guava:31.1-jre\"}]}");

        Assert.False(SkinAgentProvider.SupportsSessionHostOverride(version));
    }
}
