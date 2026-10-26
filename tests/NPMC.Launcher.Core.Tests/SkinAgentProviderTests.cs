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

    [Fact]
    public void No_authlib_library_means_no_skin_agent()
    {
        var version = VersionJsonSerializer.Parse(
            "{\"id\":\"test\",\"libraries\":[{\"name\":\"com.google.guava:guava:31.1-jre\"}]}");

        Assert.False(SkinAgentProvider.SupportsSessionHostOverride(version));
    }
}
