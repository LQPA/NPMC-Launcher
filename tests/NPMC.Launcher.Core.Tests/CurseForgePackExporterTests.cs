using System.Text.Json;
using NPMC.Launcher.Infrastructure.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class CurseForgePackExporterTests
{
    [Fact]
    public void Manifest_contains_loader_game_and_file_ids()
    {
        var json = CurseForgePackExporter.BuildManifestJson(
            "My Pack", "1.2.3", "tester", "1.20.1", "forge", "47.4.20",
            new[] { (ProjectId: 238222, FileId: 2673310) });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("minecraftModpack", root.GetProperty("manifestType").GetString());
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("My Pack", root.GetProperty("name").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("tester", root.GetProperty("author").GetString());
        Assert.Equal("overrides", root.GetProperty("overrides").GetString());

        var minecraft = root.GetProperty("minecraft");
        Assert.Equal("1.20.1", minecraft.GetProperty("version").GetString());

        var loader = minecraft.GetProperty("modLoaders")[0];
        Assert.Equal("forge-47.4.20", loader.GetProperty("id").GetString());
        Assert.True(loader.GetProperty("primary").GetBoolean());

        var file = root.GetProperty("files")[0];
        Assert.Equal(238222, file.GetProperty("projectID").GetInt32());
        Assert.Equal(2673310, file.GetProperty("fileID").GetInt32());
        Assert.True(file.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Vanilla_manifest_has_empty_loader_list()
    {
        var json = CurseForgePackExporter.BuildManifestJson(
            "Vanilla", "1.0.0", "tester", "1.21.1", null, null, Array.Empty<(int, int)>());

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("minecraft").GetProperty("modLoaders").GetArrayLength());
        Assert.Equal(0, root.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void Loader_without_version_uses_the_bare_key()
    {
        var json = CurseForgePackExporter.BuildManifestJson(
            "Pack", "1.0.0", "tester", "1.20.1", "neoforge", "", Array.Empty<(int, int)>());

        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("minecraft")
            .GetProperty("modLoaders")[0].GetProperty("id").GetString();

        Assert.Equal("neoforge", id);
    }
}
