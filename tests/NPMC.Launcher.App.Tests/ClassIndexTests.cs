using System.IO.Compression;
using System.Text;
using NPMC.Launcher.App.Mods;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 包名索引（类 → 模组 jar 的反查缓存）。
///
/// 存在的理由：诊断要把 NoClassDefFoundError 的类名反查到模组，原来每次都遍历几百个 jar。
/// 索引按 jar 的 (路径, 大小, 修改时间) 缓存包名集合，查询变成字典查找；
/// 同时"任何 jar 都没有这个包前缀"可以直接判否定，不必扫盘。
/// </summary>
public class ClassIndexTests
{
    [Fact]
    public void Finds_the_jar_that_owns_the_package()
    {
        var root = CreateTempRoot();
        try
        {
            var sodiumJar = Path.Combine(root, "embeddium.jar");
            var otherJar = Path.Combine(root, "other.jar");
            WriteJar(sodiumJar, "net/caffeinemc/mods/sodium/api/memory/MemoryIntrinsics.class");
            WriteJar(otherJar, "com/example/mod/Thing.class");

            var index = ClassIndex.Build(new[] { sodiumJar, otherJar });

            Assert.True(index.TryFindProvider(
                "net/caffeinemc/mods/sodium/api/memory/MemoryIntrinsics", out var jar, out var package));
            Assert.Equal(sodiumJar, jar);
            Assert.Equal("net/caffeinemc/mods/sodium/api/memory", package);

            // 类的写法（点号）也要能查
            Assert.True(index.TryFindProvider("com.example.mod.Thing", out var other, out _));
            Assert.Equal(otherJar, other);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Unknown_package_is_a_fast_negative()
    {
        var root = CreateTempRoot();
        try
        {
            var jar = Path.Combine(root, "mod.jar");
            WriteJar(jar, "com/example/mod/Thing.class");

            var index = ClassIndex.Build(new[] { jar });

            Assert.False(index.TryFindProvider("net/caffeinemc/mods/sodium/api/Foo", out _, out _));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Cache_is_reused_on_second_build()
    {
        var root = CreateTempRoot();
        try
        {
            var jar = Path.Combine(root, "mod.jar");
            WriteJar(jar, "com/example/mod/Thing.class");
            var cachePath = Path.Combine(root, "cache", "class-index.json");

            var first = ClassIndex.Build(new[] { jar }, cachePath);
            Assert.True(File.Exists(cachePath));

            var second = ClassIndex.Build(new[] { jar }, cachePath);
            Assert.Equal(first.JarCount, second.JarCount);
            Assert.True(second.TryFindProvider("com.example.mod.Thing", out var found, out _));
            Assert.Equal(jar, found);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Broken_cache_file_falls_back_to_a_rebuild()
    {
        var root = CreateTempRoot();
        try
        {
            var jar = Path.Combine(root, "mod.jar");
            WriteJar(jar, "com/example/mod/Thing.class");
            var cachePath = Path.Combine(root, "class-index.json");
            File.WriteAllText(cachePath, "{ 坏掉的 json");

            var index = ClassIndex.Build(new[] { jar }, cachePath);
            Assert.True(index.TryFindProvider("com.example.mod.Thing", out _, out _));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-classindex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteJar(string jarPath, params string[] entryNames)
    {
        using var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create);
        foreach (var name in entryNames)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("dummy");
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响结论
        }
    }
}
