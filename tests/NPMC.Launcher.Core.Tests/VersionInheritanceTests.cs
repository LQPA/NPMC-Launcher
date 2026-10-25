using NPMC.Launcher.Core.Models;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 加载器 profile 与父版本的合并。这段错了，Fabric 实例要么缺库要么找不到 jar。
/// </summary>
public class VersionInheritanceTests
{
    private static Library Lib(string name) => new() { Name = name };

    private static ArgumentEntry Arg(string value) => new(null, new[] { value });

    private static VersionJson Vanilla() => new()
    {
        Id = "1.20.1",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "5",
        AssetIndex = new AssetIndexRef { Id = "5", Url = "https://example/5.json" },
        Libraries = new List<Library> { Lib("com.mojang:authlib:4.0.43"), Lib("org.ow2.asm:asm:9.6") },
        Arguments = new VersionArguments
        {
            Game = new List<ArgumentEntry> { Arg("--gameDir") },
            Jvm = new List<ArgumentEntry> { Arg("-Djava.library.path") },
        },
    };

    private static VersionJson FabricProfile() => new()
    {
        Id = "fabric-loader-0.20.1-1.20.1",
        InheritsFrom = "1.20.1",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        Libraries = new List<Library> { Lib("org.ow2.asm:asm:9.10.1"), Lib("net.fabricmc:fabric-loader:0.20.1") },
        Arguments = new VersionArguments
        {
            Game = new List<ArgumentEntry>(),
            Jvm = new List<ArgumentEntry> { Arg("-DFabricMcEmu") },
        },
    };

    [Fact]
    public void Child_main_class_wins()
    {
        var merged = VersionInheritance.Merge(FabricProfile(), Vanilla());

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);
    }

    [Fact]
    public void Missing_fields_fall_back_to_the_parent()
    {
        var merged = VersionInheritance.Merge(FabricProfile(), Vanilla());

        // profile 不写资源索引，必须继承父版本，否则游戏没有资源
        Assert.Equal("5", merged.Assets);
        Assert.NotNull(merged.AssetIndex);
        Assert.Equal("5", merged.AssetIndex!.Id);
        Assert.Equal("release", merged.Type);
    }

    [Fact]
    public void Child_library_replaces_the_same_artifact()
    {
        var merged = VersionInheritance.Merge(FabricProfile(), Vanilla());

        var asm = merged.Libraries.Where(library => VersionInheritance.ArtifactKey(library.Name) == "org.ow2.asm:asm").ToList();
        Assert.Single(asm);
        Assert.Equal("org.ow2.asm:asm:9.10.1", asm[0].Name);

        // 父版本独有的库必须保留
        Assert.Contains(merged.Libraries, library => library.Name == "com.mojang:authlib:4.0.43");
        Assert.Contains(merged.Libraries, library => library.Name == "net.fabricmc:fabric-loader:0.20.1");
    }

    [Fact]
    public void Arguments_are_concatenated_parent_first()
    {
        var merged = VersionInheritance.Merge(FabricProfile(), Vanilla());

        Assert.Equal(new[] { "--gameDir" }, merged.Arguments!.Game.Select(entry => entry.Values[0]));
        Assert.Equal(
            new[] { "-Djava.library.path", "-DFabricMcEmu" },
            merged.Arguments.Jvm.Select(entry => entry.Values[0]));
    }

    [Fact]
    public void InheritsFrom_is_cleared_after_merging()
    {
        Assert.Null(VersionInheritance.Merge(FabricProfile(), Vanilla()).InheritsFrom);
    }

    [Theory]
    [InlineData("org.ow2.asm:asm:9.6", "org.ow2.asm:asm")]
    [InlineData("org.ow2.asm:asm:9.6:natives", "org.ow2.asm:asm")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Artifact_key_ignores_version_and_classifier(string coordinate, string expected)
    {
        Assert.Equal(expected, VersionInheritance.ArtifactKey(coordinate));
    }
}
