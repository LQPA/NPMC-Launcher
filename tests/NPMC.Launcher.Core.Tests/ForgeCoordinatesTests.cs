using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// Forge 安装包的坐标与 profile 解析。
///
/// 这块最容易出错的地方是 **Maven 版本号的命名**：老版本把 MC 版本写了两遍
/// （<c>1.7.10-10.13.4.1614-1.7.10</c>），新版本只写一遍。
/// 猜错就只会拿到 404，所以两个候选都要试。
/// </summary>
public class ForgeCoordinatesTests
{
    [Fact]
    public void Both_maven_naming_schemes_are_offered()
    {
        var candidates = ForgeCoordinates.MavenVersionCandidates("1.7.10", "10.13.4.1614");

        Assert.Equal(2, candidates.Count);
        Assert.Contains("1.7.10-10.13.4.1614", candidates);
        Assert.Contains("1.7.10-10.13.4.1614-1.7.10", candidates);
    }

    [Fact]
    public void Installer_urls_point_at_the_forge_maven()
    {
        var urls = ForgeCoordinates.InstallerUrlCandidates("1.7.10", "10.13.4.1614");

        Assert.Equal(2, urls.Count);
        Assert.All(urls, url =>
        {
            Assert.StartsWith("https://maven.minecraftforge.net/net/minecraftforge/forge/", url);
            Assert.EndsWith("-installer.jar", url);
        });

        // 老版本那条链接里 MC 版本出现两次
        Assert.Contains(urls, url => url.Contains("1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10-installer.jar"));
    }

    private const string LegacyProfile = """
    {
      "install": {
        "profileName": "forge",
        "minecraft": "1.7.10",
        "filePath": "forge-1.7.10-10.13.4.1614-1.7.10-universal.jar"
      },
      "versionInfo": {
        "id": "1.7.10-Forge10.13.4.1614-1.7.10",
        "inheritsFrom": "1.7.10",
        "mainClass": "net.minecraft.launchwrapper.Launch",
        "minecraftArguments": "--username ${auth_player_name}",
        "libraries": [
          { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", "url": "https://maven.minecraftforge.net/" },
          { "name": "net.minecraft:launchwrapper:1.12" }
        ]
      }
    }
    """;

    [Fact]
    public void Legacy_profile_can_be_installed_offline()
    {
        var profile = ForgeCoordinates.Parse(LegacyProfile, "fallback");

        Assert.True(profile.CanInstallOffline);
        Assert.False(profile.RequiresProcessors);
        Assert.Equal("1.7.10-Forge10.13.4.1614-1.7.10", profile.VersionId);
        Assert.Equal("1.7.10", profile.MinecraftVersion);
        Assert.Equal("net.minecraft.launchwrapper.Launch", profile.Version.MainClass);
        Assert.Equal("1.7.10", profile.Version.InheritsFrom);
        Assert.Equal("forge-1.7.10-10.13.4.1614-1.7.10-universal.jar", profile.UniversalJarEntry);
        Assert.Equal(2, profile.Version.Libraries.Count);
        Assert.Equal("https://maven.minecraftforge.net/", profile.Version.Libraries[0].Url);
    }

    [Fact]
    public void Modern_profile_with_processors_is_not_installable_offline()
    {
        var profile = ForgeCoordinates.Parse("""
        {
          "spec": 1,
          "processors": [ { "sides": [ "client" ], "jar": "a.jar", "classpath": [], "args": [] } ],
          "data": {},
          "libraries": [ { "name": "net.minecraftforge:forge:1.20.1-47.4.28" } ]
        }
        """, "1.20.1-forge47.4.28");

        Assert.True(profile.RequiresProcessors);
        Assert.False(profile.CanInstallOffline);
        Assert.Equal("1.20.1-forge47.4.28", profile.VersionId);   // 回退到调用方给的 id
    }

    [Fact]
    public void Library_coordinates_become_maven_paths()
    {
        Assert.Equal(
            "net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10.jar",
            ForgeCoordinates.LibraryPath("net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10"));
    }

    [Fact]
    public void Broken_json_is_reported_not_swallowed()
    {
        Assert.ThrowsAny<Exception>(() => ForgeCoordinates.Parse("{ not json", "x"));
    }
}
