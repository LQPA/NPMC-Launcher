using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 升级判定的纯逻辑。这里不联网，全部用手工构造的候选版本。
/// 判定现在与平台无关，所以同一套逻辑对 Modrinth / CurseForge 通用。
/// </summary>
public class UpdatePlannerTests
{
    private static UpdateCandidate Candidate(
        string versionLabel,
        string publishedAt,
        bool prerelease = false,
        bool withUrl = true,
        string projectId = "p",
        UpdateSource source = UpdateSource.Modrinth) => new()
        {
            Source = source,
            ProjectId = projectId,
            VersionLabel = versionLabel,
            PublishedAt = DateTimeOffset.Parse(publishedAt),
            FileName = $"{versionLabel}.jar",
            DownloadUrl = withUrl ? $"https://cdn/{versionLabel}.jar" : "",
            Channel = prerelease ? ReleaseChannel.Beta : ReleaseChannel.Release,
        };

    private static Dictionary<UpdateProjectKey, IReadOnlyList<UpdateCandidate>> Versions(
        params UpdateCandidate[] candidates)
        => candidates
            .GroupBy(candidate => candidate.Key)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<UpdateCandidate>)group.ToList());

    [Fact]
    public void Finds_the_newest_release_after_the_current_one()
    {
        var current = Candidate("1.0", "2026-01-01");
        var candidates = new[]
        {
            current,
            Candidate("1.1", "2026-02-01"),
            Candidate("1.2", "2026-03-01"),
            Candidate("0.9", "2025-12-01"),
        };

        var newer = UpdatePlanner.FindNewer(current, candidates);

        Assert.NotNull(newer);
        Assert.Equal("1.2", newer!.VersionLabel);
    }

    [Fact]
    public void Returns_null_when_nothing_is_newer()
    {
        var current = Candidate("1.2", "2026-03-01");

        Assert.Null(UpdatePlanner.FindNewer(current, new[] { current, Candidate("1.0", "2026-01-01") }));
    }

    [Fact]
    public void Skips_prereleases_unless_asked()
    {
        var current = Candidate("1.0", "2026-01-01");
        var candidates = new[] { Candidate("2.0-beta", "2026-03-01", prerelease: true) };

        Assert.Null(UpdatePlanner.FindNewer(current, candidates));
        Assert.NotNull(UpdatePlanner.FindNewer(current, candidates, includePrerelease: true));
    }

    [Fact]
    public void Plan_ignores_candidates_without_a_download_url()
    {
        var current = Candidate("1.0", "2026-01-01");
        var blocked = Candidate("1.1", "2026-03-01", withUrl: false);

        var plan = UpdatePlanner.Plan(
            new[] { new LocalModInfo { Path = "/mods/a.jar", FileName = "a.jar", Sha1 = "S", Matched = current } },
            Versions(current, blocked));

        // 拿不到下载地址（CurseForge 禁止第三方下载时就是这种）→ 不建议升级
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_skips_mods_that_no_platform_could_match()
    {
        var plan = UpdatePlanner.Plan(
            new[] { new LocalModInfo { Path = "/mods/x.jar", FileName = "x.jar", Sha1 = "DEAD", Matched = null } },
            new Dictionary<UpdateProjectKey, IReadOnlyList<UpdateCandidate>>());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_produces_one_update_per_outdated_mod()
    {
        var current = Candidate("1.0", "2026-01-01");
        var latest = Candidate("1.1", "2026-02-01");

        var plan = UpdatePlanner.Plan(
            new[] { new LocalModInfo { Path = "/mods/a.jar", FileName = "a.jar", Sha1 = "S", Matched = current } },
            Versions(current, latest));

        var update = Assert.Single(plan);
        Assert.Equal("a.jar", update.FileName);
        Assert.Equal("1.0", update.Current.VersionLabel);
        Assert.Equal("1.1", update.Latest.VersionLabel);
        Assert.Equal(UpdateSource.Modrinth, update.Source);
        Assert.Contains("1.0 → 1.1", update.Description);
    }

    [Fact]
    public void Plan_does_not_borrow_versions_from_another_source()
    {
        // 两个平台用了同样的项目 id 与版本号：字典键带来源，绝不能互相污染
        var current = Candidate("1.0", "2026-01-01", projectId: "123", source: UpdateSource.CurseForge);
        var foreignNewer = Candidate("1.1", "2026-03-01", projectId: "123", source: UpdateSource.Modrinth);

        var plan = UpdatePlanner.Plan(
            new[] { new LocalModInfo { Path = "/mods/a.jar", FileName = "a.jar", Sha1 = "S", Matched = current } },
            Versions(foreignNewer));

        Assert.Empty(plan);
    }

    [Fact]
    public void Source_is_reported_from_the_latest_candidate()
    {
        var current = Candidate("1.0", "2026-01-01", projectId: "123", source: UpdateSource.CurseForge);
        var newer = Candidate("1.1", "2026-02-01", projectId: "123", source: UpdateSource.CurseForge);

        var plan = UpdatePlanner.Plan(
            new[] { new LocalModInfo { Path = "/mods/a.jar", FileName = "a.jar", Sha1 = "S", Matched = current } },
            Versions(current, newer));

        Assert.Equal(UpdateSource.CurseForge, Assert.Single(plan).Source);
    }
}

public class ModrinthCandidateMappingTests
{
    private static ModrinthVersion Version(string number, string publishedAt, bool withFile = true) => new(
        Id: "v-" + number,
        ProjectId: "AANobbMI",
        VersionNumber: number,
        Name: number,
        VersionType: "release",
        PublishedAt: DateTimeOffset.Parse(publishedAt),
        GameVersions: new[] { "1.20.1" },
        Loaders: new[] { "fabric" },
        Files: withFile
            ? new[] { new ModrinthFile($"{number}.jar", $"https://cdn/{number}.jar", "ABCDEF", 1024, true) }
            : Array.Empty<ModrinthFile>());

    [Fact]
    public void Maps_every_relevant_field()
    {
        var candidate = Version("1.2.3", "2026-02-01").ToCandidate();

        Assert.NotNull(candidate);
        Assert.Equal(UpdateSource.Modrinth, candidate!.Source);
        Assert.Equal("AANobbMI", candidate.ProjectId);
        Assert.Equal("1.2.3", candidate.VersionLabel);
        Assert.Equal("1.2.3.jar", candidate.FileName);
        Assert.Equal("https://cdn/1.2.3.jar", candidate.DownloadUrl);
        Assert.Equal("ABCDEF", candidate.Sha1);
        Assert.False(candidate.IsPrerelease);
    }

    [Fact]
    public void Returns_null_when_there_is_no_downloadable_file()
    {
        Assert.Null(Version("1.2.3", "2026-02-01", withFile: false).ToCandidate());
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
        Assert.Equal(new[] { "fabric" },
            LoaderDetector.Detect(WithLibraries("net.fabricmc:fabric-loader:0.15.11")));
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
        Assert.Equal(new[] { "quilt" },
            LoaderDetector.Detect(WithLibraries("org.quiltmc:quilt-loader:0.23.1")));
    }

    [Fact]
    public void Returns_empty_for_missing_libraries()
    {
        Assert.Empty(LoaderDetector.Detect(new VersionJson { Id = "x", Type = "release", MainClass = "m" }));
    }
}
