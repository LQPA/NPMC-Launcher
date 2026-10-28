using System.IO.Compression;
using NPMC.Launcher.Infrastructure.Launch;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 自定义横幅（主界面标语）通过资源包覆盖 splashes.txt 实现；
/// options.txt 的写法在 1.13 前后不同，这里把两条路径都钉住。
/// </summary>
public class SplashTextPackTests
{
    [Theory]
    [InlineData("1.7.10", false, 1)]
    [InlineData("1.12.2", false, 3)]
    [InlineData("1.16.5", true, 6)]
    [InlineData("1.20.1", true, 15)]
    [InlineData("1.21.1", true, 34)]
    [InlineData("1.21.11", true, 69)]
    public void Pack_format_follows_the_game_version(string version, bool modern, int expected)
    {
        Assert.Equal(expected, SplashTextPack.PackFormat(version, modern));
    }

    [Fact]
    public void Modern_options_get_file_entry_and_keep_existing_packs()
    {
        var updated = SplashTextPack.RewriteOptions(
            "version:3955\nresourcePacks:[\"vanilla\",\"file/other.zip\"]\nincompatibleResourcePacks:[]\n",
            modern: true,
            enable: true);

        Assert.Contains("resourcePacks:[\"vanilla\",\"file/other.zip\",\"file/nmpc-splash.zip\"]", updated);
        Assert.Contains("incompatibleResourcePacks:[]", updated);
    }

    [Fact]
    public void Legacy_options_use_a_bare_file_name()
    {
        var updated = SplashTextPack.RewriteOptions(
            "resourcePacks:[\"existing.zip\"]\n",
            modern: false,
            enable: true);

        Assert.Contains("resourcePacks:[\"existing.zip\",\"nmpc-splash.zip\"]", updated);
    }

    [Fact]
    public void Enabling_moves_the_entry_out_of_incompatible_packs()
    {
        var updated = SplashTextPack.RewriteOptions(
            "resourcePacks:[\"vanilla\"]\nincompatibleResourcePacks:[\"file/nmpc-splash.zip\"]\n",
            modern: true,
            enable: true);

        Assert.Contains("incompatibleResourcePacks:[]", updated);
    }

    [Fact]
    public void Disabling_removes_only_our_entry()
    {
        var updated = SplashTextPack.RewriteOptions(
            "resourcePacks:[\"vanilla\",\"file/nmpc-splash.zip\",\"file/other.zip\"]\n",
            modern: true,
            enable: false);

        Assert.DoesNotContain("nmpc-splash.zip", updated);
        Assert.Contains("file/other.zip", updated);
    }

    [Fact]
    public void Apply_writes_pack_and_options_then_clears_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), "npmc-splash-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "resourcepacks"));

            SplashTextPack.Apply(dir, "1.21.1", modern: true, "我的自定义横幅");

            var pack = Path.Combine(dir, "resourcepacks", SplashTextPack.PackFileName);
            Assert.True(File.Exists(pack));

            var options = File.ReadAllText(Path.Combine(dir, "options.txt"));
            Assert.Contains("file/nmpc-splash.zip", options);

            using (var archive = ZipFile.OpenRead(pack))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName == "pack.mcmeta");
                Assert.Contains(archive.Entries, entry => entry.FullName == "assets/minecraft/texts/splashes.txt");

                var meta = ReadEntry(archive, "pack.mcmeta");
                Assert.Contains("\"pack_format\":34", meta);

                var splash = ReadEntry(archive, "assets/minecraft/texts/splashes.txt");
                Assert.Equal("我的自定义横幅", splash.Trim());
            }

            // 清空 → 资源包删除、条目移除
            SplashTextPack.Apply(dir, "1.21.1", modern: true, null);

            Assert.False(File.Exists(pack));
            Assert.DoesNotContain("nmpc-splash.zip", File.ReadAllText(Path.Combine(dir, "options.txt")));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.Entries.First(item => item.FullName == name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
