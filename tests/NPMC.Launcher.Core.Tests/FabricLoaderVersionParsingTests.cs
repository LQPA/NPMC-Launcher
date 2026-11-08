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
    /// <summary>
    /// 26.x 的接口会把该游戏版本所有条目的 intermediary 都写成占位符 0.0.0，
    /// 但 /versions/loader/26.2/0.20.1/profile/json 正常返回 —— 说明这个接口对 26.x
    /// 不提供映射信息，而不是"装不上"。全部是占位符时必须放行，否则 26.x 的 Fabric
    /// 会被整体判死（2026-11-08 实测：Fabulously Optimized 14.1.2 报「没有适用于 26.2 的加载器」）。
    /// </summary>
    [Fact]
    public void Keeps_all_entries_when_every_intermediary_is_the_placeholder()
    {
        const string response = """
        [
          { "loader": { "version": "0.20.1", "stable": true }, "intermediary": { "version": "0.0.0" }, "launcherMeta": {} },
          { "loader": { "version": "0.20.0", "stable": true }, "intermediary": { "version": "0.0.0" }, "launcherMeta": {} }
        ]
        """;

        var versions = FabricLoaderInstaller.ParseLoaderVersions(response);

        Assert.Equal(2, versions.Count);
        Assert.Equal("0.20.1", versions[0].Version);
    }
}