using NPMC.Launcher.Infrastructure.Launch;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// Java 版本解析。注意 Java 8 报的是 "1.8"，必须归一化成 8，
/// 否则"要求 17+" 的判断会因为 1 &lt; 17 而碰巧正确、但显示出来的版本号是错的。
/// </summary>
public class JavaLocatorTests
{
    [Fact]
    public void Parses_modern_specification_version()
    {
        const string output = """
            Property settings:
                java.home = C:\Java\jdk-17
                java.specification.version = 17
                java.version = 17.0.15
            """;

        Assert.Equal(17, JavaLocator.ParseMajorVersion(output));
    }

    [Fact]
    public void Parses_java_21()
    {
        const string output = "    java.specification.version = 21\n";
        Assert.Equal(21, JavaLocator.ParseMajorVersion(output));
    }

    [Fact]
    public void Normalizes_legacy_1_8_to_8()
    {
        const string output = """
            Property settings:
                java.specification.version = 1.8
                java.version = 1.8.0_402
            """;

        Assert.Equal(8, JavaLocator.ParseMajorVersion(output));
    }

    [Fact]
    public void Handles_underscore_in_version_value()
    {
        const string output = "java.specification.version = 1_8";
        Assert.Equal(8, JavaLocator.ParseMajorVersion(output));
    }

    [Fact]
    public void Returns_null_when_property_is_absent()
    {
        Assert.Null(JavaLocator.ParseMajorVersion("openjdk version \"17\"\n"));
        Assert.Null(JavaLocator.ParseMajorVersion(""));
        Assert.Null(JavaLocator.ParseMajorVersion(null));
    }

    [Fact]
    public void Ignores_surrounding_noise()
    {
        const string output = """
            openjdk version "17.0.15" 2025-04-15 LTS
            OpenJDK Runtime Environment Microsoft-11369865 (build 17.0.15+6-LTS)
            Property settings:
                file.encoding = UTF-8
                java.specification.version = 17
                sun.jnu.encoding = UTF-8
            """;

        Assert.Equal(17, JavaLocator.ParseMajorVersion(output));
    }
}
