using System.Text;
using NPMC.Launcher.Infrastructure.Storage;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 缓存统计与清理。
///
/// 背景：<c>data\cache</c> 下整合包解压产物 + Forge 安装包 + 安装器临时目录
/// 加起来能到 1 GB（实测 400 + 380 + 126 + 94 MB），界面上完全看不到。
/// 这里只验证"统计正确、只清可再生的、译文表不动"。
/// </summary>
public class CacheInventoryTests
{
    [Fact]
    public void Measures_each_area_and_marks_what_is_safe_to_clear()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFile(root, "icons/a.img", 100);
            WriteFile(root, "icons/b.img", 200);
            WriteFile(root, "modpacks/x/overrides.txt", 500);
            WriteFile(root, "translations/translations.csv", 50);

            var areas = CacheInventory.Measure(root);

            var icons = areas.Single(area => area.Key == "icons");
            Assert.Equal(300, icons.Bytes);
            Assert.Equal(2, icons.Files);
            Assert.True(icons.SafeToClear);

            var translations = areas.Single(area => area.Key == "translations");
            Assert.Equal(50, translations.Bytes);
            Assert.False(translations.SafeToClear);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Clear_skips_translations_and_removes_regenerable_caches()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFile(root, "icons/a.img", 100);
            WriteFile(root, "modpacks/x/a.txt", 100);
            WriteFile(root, "forge/installer.jar", 100);
            WriteFile(root, "translations/translations.csv", 50);

            var result = CacheInventory.ClearSafe(root);

            Assert.Equal(3, result.Files);
            Assert.Equal(300, result.Bytes);
            Assert.True(File.Exists(Path.Combine(root, "translations", "translations.csv")));
            Assert.False(Directory.Exists(Path.Combine(root, "icons")) &&
                         Directory.EnumerateFiles(Path.Combine(root, "icons"), "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Stale_temp_entries_are_removed_but_fresh_ones_kept()
    {
        var root = CreateTempRoot();
        try
        {
            var oldFile = WriteFile(root, "old.txt", 10);
            var freshFile = WriteFile(root, "fresh.txt", 10);
            var oldDir = Path.Combine(root, "old-dir");
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "x.txt"), "x");

            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));
            Directory.SetLastWriteTimeUtc(oldDir, DateTime.UtcNow.AddDays(-10));

            var removed = CacheInventory.CleanStaleEntries(root, TimeSpan.FromDays(3));

            Assert.Equal(2, removed);
            Assert.False(File.Exists(oldFile));
            Assert.False(Directory.Exists(oldDir));
            Assert.True(File.Exists(freshFile));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Formats_sizes_readably()
    {
        Assert.Equal("512 B", CacheInventory.FormatSize(512));
        Assert.Equal("2 KB", CacheInventory.FormatSize(2048));
        Assert.Equal("1.0 MB", CacheInventory.FormatSize(1024 * 1024));
        Assert.Equal("1.50 GB", CacheInventory.FormatSize((long)(1.5 * 1024 * 1024 * 1024)));
    }

    private static string WriteFile(string root, string relative, int bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', bytes)));
        return path;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
