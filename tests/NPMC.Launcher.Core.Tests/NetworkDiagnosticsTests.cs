using NPMC.Launcher.Infrastructure.Network;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// fake-IP 判定。只用于识别"TUN 是否接管了 DNS"，
/// **不能**用来断定流量走了代理 —— 见 NetworkDiagnostics 的注释。
/// </summary>
public class NetworkDiagnosticsTests
{
    [Fact]
    public void Detects_fake_ip_range()
    {
        Assert.True(NetworkDiagnostics.IsFakeIp("198.18.0.1"));
        Assert.True(NetworkDiagnostics.IsFakeIp("198.18.0.146"));
        Assert.True(NetworkDiagnostics.IsFakeIp("198.19.255.254"));
    }

    [Fact]
    public void Does_not_flag_neighbouring_ranges()
    {
        Assert.False(NetworkDiagnostics.IsFakeIp("198.17.0.1"));
        Assert.False(NetworkDiagnostics.IsFakeIp("198.20.0.1"));
        Assert.False(NetworkDiagnostics.IsFakeIp("197.18.0.1"));
    }

    [Fact]
    public void Does_not_flag_real_public_addresses()
    {
        Assert.False(NetworkDiagnostics.IsFakeIp("150.171.110.137"));
        Assert.False(NetworkDiagnostics.IsFakeIp("8.8.8.8"));
    }

    [Fact]
    public void Ignores_ipv6_and_unparsable_input()
    {
        Assert.False(NetworkDiagnostics.IsFakeIp("2606:4700::1111"));
        Assert.False(NetworkDiagnostics.IsFakeIp("not-an-ip"));
        Assert.False(NetworkDiagnostics.IsFakeIp(""));
        Assert.False(NetworkDiagnostics.IsFakeIp(null));
    }

    [Fact]
    public void Resolution_reports_fake_ip_when_any_system_address_is_in_range()
    {
        var resolution = new NetworkResolution(
            "example.com",
            new[] { "198.18.0.71", "2606:4700::1111" },
            new[] { "93.184.216.34" });

        Assert.True(resolution.HasFakeIp);
    }
}
