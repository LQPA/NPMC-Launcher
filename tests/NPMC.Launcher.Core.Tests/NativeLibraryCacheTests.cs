using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Infrastructure.Launch;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// LWJGL 3.4 改成 &lt;build&gt;/&lt;arch&gt;/ 缓存布局后，extract 目录里残留的平铺 dll 会让它的
/// 平台探测认错架构（26.3 实测报 windows/arm64，然后 FML 早期窗口崩掉）。
/// </summary>
public class NativeLibraryCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "npmc-native-cache-" + Guid.NewGuid().ToString("N"));

    private static VersionJson VersionWithLwjgl(string version) => new()
    {
        Id = "test",
        Libraries = new List<Library> { new() { Name = $"org.lwjgl:lwjgl:{version}" } },
    };

    private static IReadOnlyList<string> ArgsFor(string directory) =>
        new[] { $"-Dorg.lwjgl.system.SharedLibraryExtractPath={directory}" };

    /// <summary>造出"平铺旧文件 + 新布局子目录"混合的缓存目录。</summary>
    private string CreateMixedCache()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "lwjgl.dll"), "stale");
        File.WriteAllText(Path.Combine(_root, "OpenAL.dll"), "stale");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "keep me");

        var versioned = Path.Combine(_root, "3.4.3+4", "x64");
        Directory.CreateDirectory(versioned);
        File.WriteAllText(Path.Combine(versioned, "lwjgl.dll"), "current");

        return _root;
    }

    [Fact]
    public void Flat_libraries_are_removed_and_the_versioned_cache_is_kept()
    {
        var directory = CreateMixedCache();

        var removed = NativeLibraryCache.RemoveStaleFlatFiles(VersionWithLwjgl("3.4.3"), ArgsFor(directory));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(Path.Combine(directory, "lwjgl.dll")));
        Assert.False(File.Exists(Path.Combine(directory, "OpenAL.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "3.4.3+4", "x64", "lwjgl.dll")));
    }

    [Fact]
    public void Older_lwjgl_keeps_the_flat_layout_it_uses_itself()
    {
        var directory = CreateMixedCache();

        var removed = NativeLibraryCache.RemoveStaleFlatFiles(VersionWithLwjgl("3.3.3"), ArgsFor(directory));

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(directory, "lwjgl.dll")));
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var removed = NativeLibraryCache.RemoveStaleFlatFiles(VersionWithLwjgl("3.4.3"), ArgsFor(missing));

        Assert.Equal(0, removed);
    }

    [Fact]
    public void Arguments_without_an_extract_path_are_ignored()
    {
        var directory = CreateMixedCache();

        var removed = NativeLibraryCache.RemoveStaleFlatFiles(
            VersionWithLwjgl("3.4.3"),
            new[] { "-Xmx4G", "-Djava.library.path=" + directory });

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(directory, "lwjgl.dll")));
    }

    [Theory]
    [InlineData("3.4.3", true)]
    [InlineData("3.4.3+4", true)]
    [InlineData("3.3.3", false)]
    [InlineData("3.2.3", false)]
    public void Only_3_4_and_newer_use_the_versioned_layout(string version, bool expected)
    {
        Assert.Equal(expected, NativeLibraryCache.UsesVersionedLayout(VersionWithLwjgl(version)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响测试结论
        }
    }
}
