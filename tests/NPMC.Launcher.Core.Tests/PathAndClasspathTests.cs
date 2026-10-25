using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 路径必须全部由根目录推导 —— 这是"整个文件夹可整体迁移"的基础。
/// </summary>
public class LauncherPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "npmc-test-root");

    [Fact]
    public void Root_is_normalized_without_trailing_separator()
    {
        var paths = new LauncherPaths(Root + Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar), paths.Root);
    }

    [Fact]
    public void Top_level_directories_are_under_root()
    {
        var paths = new LauncherPaths(Root);
        foreach (var dir in new[] { paths.Runtime, paths.Libraries, paths.Versions, paths.Assets, paths.Instances })
            Assert.StartsWith(paths.Root, dir);
    }

    [Fact]
    public void Java_exe_uses_runtime_component_folder()
    {
        var paths = new LauncherPaths(Root);
        Assert.Equal(
            Path.Combine(paths.Root, "runtime", "java-runtime-gamma", "bin", "java.exe"),
            paths.JavaExe("java-runtime-gamma"));
    }

    [Fact]
    public void Asset_object_file_uses_first_two_chars_of_hash()
    {
        var paths = new LauncherPaths(Root);
        const string hash = "abcdef0123456789";
        Assert.Equal(
            Path.Combine(paths.Root, "assets", "objects", "ab", hash),
            paths.AssetObjectFile(hash));
    }

    [Fact]
    public void Maven_path_is_converted_to_local_path()
    {
        var paths = new LauncherPaths(Root);
        Assert.Equal(
            Path.Combine(paths.Root, "libraries", "com", "google", "gson", "2.10", "gson-2.10.jar"),
            paths.LibraryFile("com/google/gson/2.10/gson-2.10.jar"));
    }

    [Fact]
    public void Version_scoped_files_follow_version_id()
    {
        var paths = new LauncherPaths(Root);
        Assert.Equal(Path.Combine(paths.Versions, "1.20.1", "1.20.1.json"), paths.VersionJson("1.20.1"));
        Assert.Equal(Path.Combine(paths.Versions, "1.20.1", "1.20.1.jar"), paths.ClientJar("1.20.1"));
        Assert.Equal(Path.Combine(paths.Versions, "1.20.1", "natives"), paths.NativesDir("1.20.1"));
    }
}

public class ClasspathBuilderTests
{
    private static readonly LauncherPaths Paths = new(Path.Combine(Path.GetTempPath(), "npmc-cp"));

    private static Library Lib(string mavenPath) => new()
    {
        Name = mavenPath,
        Downloads = new LibraryDownloads
        {
            Artifact = new DownloadArtifact { Path = mavenPath, Sha1 = "", Size = 1, Url = "" },
        },
    };

    [Fact]
    public void Client_jar_comes_last()
    {
        // 官方启动器把 client jar 放最后，Fabric 依赖这个顺序定位游戏 jar
        var classpath = ClasspathBuilder.Build(Paths, new[] { Lib("a/b/c.jar") }, "/client.jar");
        Assert.Equal("/client.jar", classpath[^1]);
        Assert.Equal(2, classpath.Count);
    }

    [Fact]
    public void Library_paths_are_mapped_under_libraries_directory()
    {
        var classpath = ClasspathBuilder.Build(
            Paths, new[] { Lib("com/google/gson/2.10/gson-2.10.jar") }, "/client.jar");

        Assert.Equal(
            Path.Combine(Paths.Root, "libraries", "com", "google", "gson", "2.10", "gson-2.10.jar"),
            classpath[0]);
    }

    [Fact]
    public void Library_without_artifact_or_coordinate_is_skipped()
    {
        // 名字不是 Maven 坐标、又没有 downloads → 无处可取，跳过
        var noArtifact = new Library { Name = "no-artifact", Downloads = null };
        var classpath = ClasspathBuilder.Build(Paths, new[] { noArtifact, Lib("a/b/c.jar") }, "/client.jar");
        Assert.Equal(2, classpath.Count);
    }

    [Fact]
    public void Maven_coordinate_library_without_downloads_is_included()
    {
        // Fabric / Quilt 的库就长这样：只有 name 与 url，没有 downloads
        var fabric = new Library { Name = "net.fabricmc:fabric-loader:0.20.1", Downloads = null };
        var classpath = ClasspathBuilder.Build(Paths, new[] { fabric }, "/client.jar");

        Assert.Equal(2, classpath.Count);
        Assert.Contains("fabric-loader-0.20.1.jar", classpath[0]);
        Assert.Equal("/client.jar", classpath[^1]);
    }

    [Fact]
    public void No_libraries_gives_client_jar_only()
        => Assert.Single(ClasspathBuilder.Build(Paths, Array.Empty<Library>(), "/client.jar"));
}
