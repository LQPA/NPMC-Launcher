using NPMC.Launcher.Core.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 整合包清单解析。这段错了，整合包要么装不上、要么把文件写到实例目录外面。
/// </summary>
public class ModpackIndexTests
{
    private const string Sample = """
    {
      "formatVersion": 1,
      "game": "minecraft",
      "versionId": "1.2.0",
      "name": "Test Pack",
      "files": [
        {
          "path": "mods/sodium.jar",
          "hashes": { "sha1": "abc123" },
          "fileSize": 1024,
          "downloads": ["https://cdn.example/sodium.jar"]
        },
        {
          "path": "resourcepacks/pack.zip",
          "hashes": { "sha1": "def456" },
          "fileSize": 2048,
          "downloads": ["https://cdn.example/pack.zip"]
        },
        {
          "path": "mods/broken.jar",
          "hashes": { "sha1": "000" },
          "fileSize": 1,
          "downloads": []
        }
      ],
      "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.20.1" }
    }
    """;

    [Fact]
    public void Parses_dependencies()
    {
        var index = ModpackIndexParser.Parse(Sample);

        Assert.Equal("1.20.1", index.GameVersion);
        Assert.Equal("fabric-loader", index.LoaderName);
        Assert.Equal("0.20.1", index.LoaderVersion);
        Assert.True(index.IsFabric);
    }

    [Fact]
    public void Parses_files_and_skips_ones_without_downloads()
    {
        var index = ModpackIndexParser.Parse(Sample);

        Assert.Equal(2, index.Files.Count);          // 没有下载地址的那条被丢弃
        Assert.Equal(1, index.ModCount);
        Assert.Equal(3072, index.TotalBytes);

        var sodium = index.Files[0];
        Assert.Equal("mods/sodium.jar", sodium.Path);
        Assert.Equal("abc123", sodium.Sha1);
        Assert.Equal(1024, sodium.FileSize);
        Assert.Single(sodium.Downloads);
    }

    [Theory]
    [InlineData("mods/a.jar", "mods/a.jar")]
    [InlineData("/mods/a.jar", "mods/a.jar")]
    [InlineData("mods\\a.jar", "mods/a.jar")]
    [InlineData("mods/sub/a.jar", "mods/sub/a.jar")]
    public void Paths_are_normalised(string input, string expected)
    {
        Assert.Equal(expected, ModpackIndexParser.NormalizePath(input));
    }

    [Theory]
    [InlineData("../evil.jar")]
    [InlineData("mods/../../evil.jar")]
    public void Traversal_paths_are_rejected(string input)
    {
        Assert.Throws<InvalidDataException>(() => ModpackIndexParser.NormalizePath(input));
    }

    [Fact]
    public void Vanilla_pack_has_no_loader()
    {
        var index = ModpackIndexParser.Parse("""
        { "name": "Vanilla", "dependencies": { "minecraft": "1.20.1" }, "files": [] }
        """);

        Assert.Null(index.LoaderName);
        Assert.False(index.IsFabric);
        Assert.Equal("原版", index.LoaderText);
        Assert.Empty(index.Files);
    }

    [Fact]
    public void Missing_name_falls_back()
    {
        var index = ModpackIndexParser.Parse("""{ "dependencies": { "minecraft": "1.20.1" } }""");

        Assert.Equal("整合包", index.Name);
    }
}
