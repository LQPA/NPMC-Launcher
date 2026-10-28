using NPMC.Launcher.Infrastructure.Minecraft;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// Quilt 的加载器清单解析：meta.quiltmc.org 的列表是**全局**的（同一个列表对任何游戏版本都返回），
/// 也没有 stable 字段，所以排序和"正式版"判定都要自己来。
/// </summary>
public class QuiltLoaderTests
{
    [Fact]
    public void Parse_sorts_newest_first_and_marks_stable_versions()
    {
        const string json = """
        [
          { "loader": { "version": "0.20.0-beta.9" } },
          { "loader": { "version": "0.29.2" } },
          { "loader": { "version": "0.20.0" } },
          { "loader": { "version": "0.30.1" } },
          { "loader": { "version": "0.29.2" } }
        ]
        """;

        var versions = QuiltLoaderInstaller.ParseLoaderVersions(json);

        Assert.Equal(
            new[] { "0.30.1", "0.29.2", "0.20.0", "0.20.0-beta.9" },
            versions.Select(version => version.Version));

        // 同一版本号重复出现时只留一条
        Assert.Equal(4, versions.Count);

        Assert.True(versions[0].Stable);
        Assert.Equal("0.30.1（stable）", versions[0].DisplayText);

        Assert.False(versions[3].Stable);
        Assert.Equal("0.20.0-beta.9", versions[3].DisplayText);
    }

    [Fact]
    public void Stable_release_comes_before_its_own_prerelease()
    {
        const string json = """
        [
          { "loader": { "version": "0.20.0-beta.9" } },
          { "loader": { "version": "0.20.0" } }
        ]
        """;

        var versions = QuiltLoaderInstaller.ParseLoaderVersions(json);

        Assert.Equal("0.20.0", versions[0].Version);
        Assert.Equal("0.20.0-beta.9", versions[1].Version);
    }

    [Fact]
    public void Non_array_payload_yields_no_versions()
    {
        Assert.Empty(QuiltLoaderInstaller.ParseLoaderVersions("{\"error\":\"nope\"}"));
    }

    [Fact]
    public void Quilt_modpacks_are_recognized()
    {
        var pack = NPMC.Launcher.Core.Modpacks.ModpackIndexParser.Parse(
            """
            {
              "name": "Example",
              "dependencies": { "minecraft": "1.21.1", "quilt-loader": "0.30.1" },
              "files": []
            }
            """);

        Assert.True(pack.IsQuilt);
        Assert.False(pack.IsFabric);
        Assert.Equal("0.30.1", pack.LoaderVersion);
    }
}
