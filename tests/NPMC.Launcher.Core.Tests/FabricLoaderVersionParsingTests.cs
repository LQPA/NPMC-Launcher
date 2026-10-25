using NPMC.Launcher.Infrastructure.Minecraft;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// Fabric 加载器列表的解析。
///
/// 关键点：Fabric 对"没有中介层映射"的游戏版本**仍然返回完整列表**，
/// 只是每条的中介层版本写成 0.0.0。照单全收的话，用户会看到一个
/// 装不起来的组合（愚人节版本就是这种情形），因此这里必须过滤掉。
/// </summary>
public class FabricLoaderVersionParsingTests
{
    private const string Response = """
    [
      {
        "loader": { "separator": ".", "build": 255, "maven": "net.fabricmc:fabric-loader:0.20.1", "version": "0.20.1", "stable": true },
        "intermediary": { "maven": "net.fabricmc:intermediary:1.20.1", "version": "1.20.1", "stable": true },
        "launcherMeta": {}
      },
      {
        "loader": { "separator": ".", "build": 250, "maven": "net.fabricmc:fabric-loader:0.19.3", "version": "0.19.3", "stable": true },
        "intermediary": { "maven": "net.fabricmc:intermediary:1.20.1", "version": "1.20.1", "stable": true },
        "launcherMeta": {}
      },
      {
        "loader": { "separator": ".", "build": 255, "maven": "net.fabricmc:fabric-loader:0.20.1", "version": "0.20.1", "stable": true },
        "intermediary": { "maven": "net.fabricmc:intermediary:0.0.0", "version": "0.0.0", "stable": true },
        "launcherMeta": {}
      },
      {
        "loader": { "separator": ".", "build": 1, "maven": "net.fabricmc:fabric-loader:0.1.0", "version": "0.1.0", "stable": false },
        "intermediary": {},
        "launcherMeta": {}
      }
    ]
    """;

    [Fact]
    public void Keeps_only_builds_with_a_real_intermediary_mapping()
    {
        var versions = FabricLoaderInstaller.ParseLoaderVersions(Response);

        // 四条里只有前两条有真实中介层版本
        Assert.Equal(2, versions.Count);
        Assert.Equal("0.20.1", versions[0].Version);
        Assert.Equal("0.19.3", versions[1].Version);
    }

    [Fact]
    public void Drops_the_placeholder_zero_mapping()
    {
        var versions = FabricLoaderInstaller.ParseLoaderVersions(Response);

        Assert.DoesNotContain(versions, version => version.Version == "0.1.0");
        // 0.20.1 出现了两次，其中一次是 0.0.0 占位；保留的是有真实映射的那条
        Assert.Single(versions.Where(version => version.Version == "0.20.1"));
    }

    [Fact]
    public void Stable_flag_is_read_from_the_loader_object()
    {
        var versions = FabricLoaderInstaller.ParseLoaderVersions(Response);

        Assert.All(versions, version => Assert.True(version.Stable));
    }

    [Fact]
    public void Non_array_response_returns_empty()
    {
        Assert.Empty(FabricLoaderInstaller.ParseLoaderVersions("""{ "error": "not found" }"""));
    }

    [Fact]
    public void Empty_array_returns_empty()
    {
        Assert.Empty(FabricLoaderInstaller.ParseLoaderVersions("[]"));
    }
}
