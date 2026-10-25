using NPMC.Launcher.Infrastructure.Launch;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 完整性标签守卫的纯逻辑部分。
///
/// 背景：Windows 用「令牌级别」与「镜像文件标签」中较低者作为进程完整性级别；
/// JDK 的 Files.isWritable 在低完整性进程里对所有文件都返回 false，
/// 于是 Fabric 的 zipfs 把重映射产物判成只读、tinyremapper 报 jar 不可写。
/// 这里只测可判定的规则，真正的 Win32 读写靠启动流程实测。
/// </summary>
public sealed class IntegrityGuardTests
{
    [Theory]
    [InlineData("S-1-16-0", WindowsIntegrityLevel.Untrusted)]
    [InlineData("S-1-16-4096", WindowsIntegrityLevel.Low)]
    [InlineData("S-1-16-8192", WindowsIntegrityLevel.Medium)]
    [InlineData("S-1-16-12288", WindowsIntegrityLevel.High)]
    [InlineData("S-1-16-16384", WindowsIntegrityLevel.System)]
    [InlineData("S-1-5-21-1-2-3-1001", WindowsIntegrityLevel.Unknown)]
    [InlineData("S-1-16-notanumber", WindowsIntegrityLevel.Unknown)]
    [InlineData("", WindowsIntegrityLevel.Unknown)]
    [InlineData(null, WindowsIntegrityLevel.Unknown)]
    public void Parses_integrity_sid(string? sid, WindowsIntegrityLevel expected)
        => Assert.Equal(expected, IntegrityGuard.LevelFromSidString(sid));

    [Theory]
    [InlineData(WindowsIntegrityLevel.Low, WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Low)]
    [InlineData(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Low, WindowsIntegrityLevel.Low)]
    [InlineData(WindowsIntegrityLevel.None, WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium)]
    [InlineData(WindowsIntegrityLevel.None, WindowsIntegrityLevel.Low, WindowsIntegrityLevel.Low)]
    [InlineData(WindowsIntegrityLevel.High, WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium)]
    [InlineData(WindowsIntegrityLevel.Unknown, WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Unknown)]
    [InlineData(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Unknown, WindowsIntegrityLevel.Unknown)]
    public void Effective_level_is_the_lower_of_image_and_token(
        WindowsIntegrityLevel image,
        WindowsIntegrityLevel token,
        WindowsIntegrityLevel expected)
        => Assert.Equal(expected, IntegrityGuard.EffectiveLevel(image, token));

    [Theory]
    // 只有「镜像标签明确更低，且启动器足够高」才需要改文件
    [InlineData(WindowsIntegrityLevel.Low, WindowsIntegrityLevel.Medium, true)]
    [InlineData(WindowsIntegrityLevel.Low, WindowsIntegrityLevel.High, true)]
    // 启动器自己就是 Low：改不了也不该乱改
    [InlineData(WindowsIntegrityLevel.Low, WindowsIntegrityLevel.Low, false)]
    // 没有标签 / 已经够高
    [InlineData(WindowsIntegrityLevel.None, WindowsIntegrityLevel.Low, false)]
    [InlineData(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium, false)]
    [InlineData(WindowsIntegrityLevel.Unknown, WindowsIntegrityLevel.Medium, false)]
    public void Needs_raise_only_for_a_lower_explicit_label(
        WindowsIntegrityLevel image,
        WindowsIntegrityLevel token,
        bool expected)
        => Assert.Equal(expected, IntegrityGuard.NeedsRaise(image, token));

    [Fact]
    public void Low_integrity_explanation_names_the_path_and_the_fix()
    {
        var text = IntegrityGuard.DescribeLowIntegrity(
            @"D:\Agents\NPMC-Launcher\.data\runtime\java-runtime-delta\bin\java.exe",
            WindowsIntegrityLevel.Low,
            WindowsIntegrityLevel.Low);

        Assert.Contains("Files.isWritable", text, StringComparison.Ordinal);
        Assert.Contains("icacls", text, StringComparison.Ordinal);
        Assert.Contains("java.exe", text, StringComparison.Ordinal);
        Assert.Contains("Low", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_executable_is_left_alone()
    {
        var directory = TestTemp.NewDirectory("integrity-missing");
        try
        {
            var result = IntegrityGuard.EnsureExecutableRunsAtLauncherLevel(
                Path.Combine(directory, "no-such-java.exe"));

            Assert.False(result.Repaired);
            Assert.Null(result.Problem);
            Assert.Equal(WindowsIntegrityLevel.Unknown, result.ProcessLevel);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Raising_a_label_on_a_missing_file_reports_an_error()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = TestTemp.NewDirectory("integrity-raise");
        try
        {
            var ok = IntegrityGuard.TryRaiseFileLevel(
                Path.Combine(directory, "no-such-java.exe"),
                WindowsIntegrityLevel.Medium,
                out var error);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }
}
