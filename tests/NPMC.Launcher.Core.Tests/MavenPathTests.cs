using NPMC.Launcher.Core.Models;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// Maven 坐标 → 路径。Fabric 的 profile JSON 只给坐标，所以这段算法错了加载器就装不上。
/// </summary>
public class MavenPathTests
{
    [Theory]
    [InlineData("net.fabricmc:intermediary:1.20.1",
        "net/fabricmc/intermediary/1.20.1/intermediary-1.20.1.jar")]
    [InlineData("net.fabricmc:fabric-loader:0.15.11",
        "net/fabricmc/fabric-loader/0.15.11/fabric-loader-0.15.11.jar")]
    [InlineData("org.ow2.asm:asm:9.6",
        "org/ow2/asm/asm/9.6/asm-9.6.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-windows",
        "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-windows@zip",
        "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.zip")]
    [InlineData("com.example:demo:1.0.0@jar",
        "com/example/demo/1.0.0/demo-1.0.0.jar")]
    public void Coordinate_becomes_repository_path(string coordinate, string expected)
    {
        Assert.Equal(expected, MavenPath.FromCoordinate(coordinate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("onlygroup")]
    [InlineData("group:artifact")]
    public void Malformed_coordinate_is_rejected(string coordinate)
    {
        Assert.ThrowsAny<ArgumentException>(() => MavenPath.FromCoordinate(coordinate));
    }
}
