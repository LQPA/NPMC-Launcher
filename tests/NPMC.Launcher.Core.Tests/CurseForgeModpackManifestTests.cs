using NPMC.Launcher.Core.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class CurseForgeModpackManifestTests
{
    [Fact]
    public void Parses_game_loader_files_and_overrides()
    {
        const string json = """
        {
          "minecraft": {
            "version": "1.12.2",
            "recommendedRam": 8192,
            "modLoaders": [
              { "id": "forge-14.23.5.2864", "primary": true }
            ]
          },
          "manifestType": "minecraftModpack",
          "manifestVersion": 1,
          "name": "Local CF Pack",
          "version": "1.2.3",
          "author": "tester",
          "files": [
            { "projectID": 238222, "fileID": 2673310, "required": true },
            { "projectID": 306770, "fileID": 1234567, "required": false }
          ],
          "overrides": "overrides"
        }
        """;

        var manifest = CurseForgeModpackManifestParser.Parse(json);

        Assert.Equal("Local CF Pack", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("1.12.2", manifest.GameVersion);
        Assert.Equal("forge", manifest.LoaderName);
        Assert.Equal("14.23.5.2864", manifest.LoaderVersion);
        Assert.Equal("overrides", manifest.OverridesDirectory);
        Assert.Equal(8192, manifest.RecommendedRam);

        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(238222, manifest.Files[0].ProjectId);
        Assert.Equal(2673310, manifest.Files[0].FileId);
        Assert.True(manifest.Files[0].Required);
        Assert.False(manifest.Files[1].Required);
    }

    [Fact]
    public void Defaults_overrides_directory_when_missing()
    {
        const string json = """
        {
          "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "fabric-0.15.11" } ] },
          "name": "No Overrides",
          "files": [ { "projectID": 1, "fileID": 2 } ]
        }
        """;

        var manifest = CurseForgeModpackManifestParser.Parse(json);

        Assert.Equal("overrides", manifest.OverridesDirectory);
        Assert.Equal("fabric", manifest.LoaderName);
        Assert.Equal("0.15.11", manifest.LoaderVersion);
    }
}
