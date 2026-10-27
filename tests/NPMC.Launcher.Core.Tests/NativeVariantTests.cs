using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Rules;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 26.3 的官方元数据把 LWJGL 的 windows 与 windows-arm64 两个 natives 变体都写成
/// "只按操作系统过滤"（没有 arch 条件），于是两条规则在 x64 机器上**都命中**。
/// 两个 jar 一起进 classpath 时 LWJGL 会认成 arm64 并报
/// "Platform/architecture mismatch ... windows/arm64"，FML 的早期窗口随之崩掉。
/// </summary>
public class NativeVariantTests
{
    private static Library Lib(string name) => new() { Name = name };

    private static IReadOnlyList<string> Select(IEnumerable<Library> libraries, string arch)
        => NativeVariants
            .SelectForArchitecture(libraries.ToList(), new OsInfo("windows", arch, "10.0"))
            .Select(library => library.Name)
            .ToList();

    [Fact]
    public void On_x64_the_plain_windows_natives_win_and_the_arm64_one_is_dropped()
    {
        var libraries = new[]
        {
            Lib("org.lwjgl:lwjgl:3.4.3"),
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows"),
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows-arm64"),
        };

        Assert.Equal(
            new[] { "org.lwjgl:lwjgl:3.4.3", "org.lwjgl:lwjgl:3.4.3:natives-windows" },
            Select(libraries, "x86_64"));
    }

    [Fact]
    public void On_arm64_the_arm64_natives_win_even_when_they_come_first()
    {
        var libraries = new[]
        {
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows-arm64"),
            Lib("org.lwjgl:lwjgl:3.4.3"),
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows"),
        };

        Assert.Equal(
            new[] { "org.lwjgl:lwjgl:3.4.3:natives-windows-arm64", "org.lwjgl:lwjgl:3.4.3" },
            Select(libraries, "arm64"));
    }

    [Fact]
    public void Each_module_is_decided_on_its_own()
    {
        var libraries = new[]
        {
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows"),
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows-arm64"),
            Lib("org.lwjgl:lwjgl-freetype:3.4.3:natives-windows"),
            Lib("org.lwjgl:lwjgl-freetype:3.4.3:natives-windows-arm64"),
        };

        Assert.Equal(
            new[]
            {
                "org.lwjgl:lwjgl:3.4.3:natives-windows",
                "org.lwjgl:lwjgl-freetype:3.4.3:natives-windows",
            },
            Select(libraries, "x86_64"));
    }

    [Fact]
    public void Ordinary_libraries_keep_their_order_and_are_never_dropped()
    {
        var libraries = new[]
        {
            Lib("com.mojang:authlib:10.0.77"),
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows-arm64"),
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows"),
            Lib("com.mojang:brigadier:1.3.11"),
        };

        Assert.Equal(
            new[]
            {
                "com.mojang:authlib:10.0.77",
                "org.lwjgl:lwjgl:3.4.3:natives-windows",
                "com.mojang:brigadier:1.3.11",
            },
            Select(libraries, "x86_64"));
    }

    [Fact]
    public void A_single_mismatched_variant_is_kept_rather_than_silently_dropped()
    {
        // 只有 arm64 一个变体时保留它：宁可让 LWJGL 自己报错，也不要凭空少一个库
        var libraries = new[]
        {
            Lib("org.lwjgl:lwjgl:3.4.3:natives-windows-arm64"),
        };

        Assert.Equal(
            new[] { "org.lwjgl:lwjgl:3.4.3:natives-windows-arm64" },
            Select(libraries, "x86_64"));
    }

    [Fact]
    public void Current_reports_the_running_architecture()
    {
        var os = OsInfo.Current();

        Assert.Equal("windows", os.Name);
        Assert.False(string.IsNullOrWhiteSpace(os.Arch));
    }
}
