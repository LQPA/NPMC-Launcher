using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 升级判定的纯逻辑。这里不联网，全部用手工构造的版本对象。
/// </summary>
public class UpdatePlannerTests
{
    private static ModrinthVersion Version(
        string id,
        string projectId,
        string number,
        string publishedAt,
        string type = "release",
        bool withFile = true) => new(
        id,
        projectId,
        number,
        $"name {number}",
        type,
        DateTimeOffset.Parse(publishedAt),
        new[] { "1.20.1" },
        new[] { "fabric" },
        withFile
            ? new[] { new ModrinthFile($"{number}.jar", $"https://cdn/{id}.jar", "SHA1", 1024, true) }
            : Array.Empty<ModrinthFile>());

    [Fact]
    public void Finds_the_newest_release_after_the_current_one()
    {
        var current = Version("v1", "p", "1.0", "2026-01-01");
        var candidates = new[]
        {
            current,
            Version("v2", "p", "1.1", "2026-02-01"),
            Version("v3", "p", "1.2", "2026-03-01"),
            Version("v0", "p", "0.9", "2025-12-01"),
        };

        var newer = UpdatePlanner.FindNewer(current, candidates);

        Assert.NotNull(newer);
        Assert.Equal("v3", newer!.Id);
    }

    [Fact]
    public void Returns_null_when_nothing_is_newer()
    {
        var current = Version("v3", "p", "1.2", "2026-03-01");
        var candidates = new[]
        {
            current,
            Version("v1", "p", "1.0", "2026-01-01"),
        };

        Assert.Null(UpdatePlanner.FindNewer(current, candidates));
    }

    [Fact]
    public void Skips_prereleases_unless_asked()
    {
        var current = Version("v1", "p", "1.0", "2026-01-01");
        var candidates = new[]
        {
            Version("v2beta", "p", "2.0-beta", "2026-03-01", type: "beta"),
        };

        Assert.Null(UpdatePlanner.FindNewer(current, candidates, includePrerelease: false));

        var withPrerelease = UpdatePlanner.FindNewer(current, candidates, includePrerelease: true);
        Assert.NotNull(withPrerelease);
        Assert.Equal("v2beta", withPrerelease!.Id);
    }

    [Fact]
    public void Ignores_candidates_without_a_downloadable_file()
    {
        var current = Version("v1", "p", "1.0", "2026-01-01");
        var candidates = new[]
        {
            Version("v2", "p", "1.1", "2026-03-01", withFile: false),
        };

        // 单个版本查询本身不管文件，Plan 才会过滤
        Assert.NotNull(UpdatePlanner.FindNewer(current, candidates));

        var plan = UpdatePlanner.Plan(
            new[]
            {
                new LocalModInfo("/mods/a.jar", "a.jar", "SHA1", current),
            },
            new Dictionary<string, IReadOnlyList<ModrinthVersion>>
            {
                ["p"] = candidates,
            });

        Assert.Empty(plan);   // 没有可下载文件 → 不建议升级
    }

    [Fact]
    public void Plan_skips_mods_that_modrinth_could_not_match()
    {
        var unknown = new LocalModInfo("/mods/unknown.jar", "unknown.jar", "DEADBEEF", null);

        var plan = UpdatePlanner.Plan(
            new[] { unknown },
            new Dictionary<string, IReadOnlyList<ModrinthVersion>>());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_produces_one_update_per_outdated_mod()
    {
        var current = Version("v1", "p", "1.0", "2026-01-01");
        var latest = Version("v2", "p", "1.1", "2026-02-01");

        var plan = UpdatePlanner.Plan(
            new[] { new LocalModInfo("/mods/a.jar", "a.jar", "SHA1", current) },
            new Dictionary<string, IReadOnlyList<ModrinthVersion>>
            {
                ["p"] = new[] { current, latest },
            });

        var update = Assert.Single(plan);
        Assert.Equal("a.jar", update.FileName);
        Assert.Equal("1.0", update.Current.VersionNumber);
        Assert.Equal("1.1", update.Latest.VersionNumber);
        Assert.Contains("1.0 → 1.1", update.Description);
    }
}

public class LoaderDetectorTests
{
    private static VersionJson WithLibraries(params string[] names) => new()
    {
        Id = "1.20.1",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Libraries = names.Select(name => new Library { Name = name }).ToList(),
    };

    [Fact]
    public void Vanilla_install_has_no_loader()
    {
        var version = WithLibraries("com.mojang:authlib:4.0.43", "org.lwjgl:lwjgl:3.3.1");

        Assert.Empty(LoaderDetector.Detect(version));
        Assert.True(LoaderDetector.IsVanilla(version));
    }

    [Fact]
    public void Detects_fabric()
    {
        var version = WithLibraries("net.fabricmc:fabric-loader:0.15.11");

        Assert.Equal(new[] { "fabric" }, LoaderDetector.Detect(version));
    }

    [Fact]
    public void Detects_forge_and_neoforge()
    {
        Assert.Equal(new[] { "forge" },
            LoaderDetector.Detect(WithLibraries("net.minecraftforge:forge:1.20.1-47.2.0")));

        Assert.Equal(new[] { "neoforge" },
            LoaderDetector.Detect(WithLibraries("net.neoforged:neoforge:20.4.237")));
    }

    [Fact]
    public void Detects_quilt_and_does_not_confuse_it_with_fabric()
    {
        var version = WithLibraries("org.quiltmc:quilt-loader:0.23.1");

        Assert.Equal(new[] { "quilt" }, LoaderDetector.Detect(version));
    }

    [Fact]
    public void Returns_empty_for_missing_libraries()
    {
        var version = new VersionJson { Id = "x", Type = "release", MainClass = "m" };

        Assert.Empty(LoaderDetector.Detect(version));
    }
}
