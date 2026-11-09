using System.Text;
using NPMC.Launcher.App.ViewModels;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 图标磁盘缓存（icons.pack + icons.idx.json）。
///
/// 原来是一个图标一个 sha1.img（实测 234 个文件 / 2.66 MB），
/// 现在打包成两个文件，按偏移取字节。
/// </summary>
public class IconStoreTests
{
    [Fact]
    public void Stores_and_reads_bytes_round_trip()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new IconStore(root);
            var first = Encoding.UTF8.GetBytes("first-image-bytes");
            var second = Encoding.UTF8.GetBytes("second-image-bytes");

            store.Store("https://example.com/a.png", first);
            store.Store("https://example.com/b.png", second);

            Assert.Equal(2, store.Count);
            Assert.True(store.TryGet("https://example.com/a.png", out var readFirst));
            Assert.Equal(first, readFirst);
            Assert.True(store.TryGet("https://example.com/b.png", out var readSecond));
            Assert.Equal(second, readSecond);
            Assert.False(store.TryGet("https://example.com/missing.png", out _));

            // 目录里只有两个文件
            var files = Directory.GetFiles(root);
            Assert.Equal(2, files.Length);
            Assert.Contains(files, path => path.EndsWith("icons.pack"));
            Assert.Contains(files, path => path.EndsWith("icons.idx.json"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Index_survives_a_restart()
    {
        var root = CreateTempRoot();
        try
        {
            var first = new IconStore(root);
            first.Store("https://example.com/a.png", Encoding.UTF8.GetBytes("payload"));

            var second = new IconStore(root);
            Assert.Equal(1, second.Count);
            Assert.True(second.TryGet("https://example.com/a.png", out var bytes));
            Assert.Equal("payload", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Legacy_img_files_are_removed_on_first_load()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var legacy = Path.Combine(root, "deadbeef.img");
            File.WriteAllBytes(legacy, new byte[] { 1, 2, 3 });

            var store = new IconStore(root);
            Assert.Equal(0, store.Count);          // 触发一次加载
            Assert.False(File.Exists(legacy));     // 旧格式无法反推 url，只能删掉重新下载
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Corrupt_index_falls_back_to_empty_cache()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "icons.idx.json"), "{ not json");

            var store = new IconStore(root);
            Assert.Equal(0, store.Count);

            store.Store("https://example.com/a.png", Array.Empty<byte>());
            Assert.Equal(0, store.Count);   // 空字节不写入
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-iconstore-tests", Guid.NewGuid().ToString("N"));
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
