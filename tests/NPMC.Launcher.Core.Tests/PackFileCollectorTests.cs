using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 「整合包自带文件」清单：清单里的文件与 overrides/ 带进来的 jar 都要算。
/// 只认清单会让 overrides 里的模组被当成用户后加的（用户实测：4 个没折叠）。
/// </summary>
public class PackFileCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "npmc-pack-files-" + Guid.NewGuid().ToString("N"));

    private string ModsDirectory
    {
        get
        {
            var directory = Path.Combine(_root, "mods");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private void Touch(string name) =>
        File.WriteAllText(Path.Combine(ModsDirectory, name), "x");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响断言
        }
    }

    [Fact]
    public void Adds_override_jars_that_are_missing_from_the_index()
    {
        Touch("from-index.jar");
        Touch("from-overrides.jar");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("from-index.jar", "abc") },
            ModsDirectory);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, entry => entry.FileName == "from-index.jar");
        Assert.Contains(result, entry => entry.FileName == "from-overrides.jar");
    }

    [Fact]
    public void Keeps_index_sha1_and_ignores_non_jar_files()
    {
        Touch("a.jar");
        Touch("readme.txt");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("a.jar", "deadbeef") },
            ModsDirectory);

        var entry = Assert.Single(result);
        Assert.Equal("a.jar", entry.FileName);
        Assert.Equal("deadbeef", entry.Sha1);
    }

    [Fact]
    public void Disabled_files_are_recorded_under_their_logical_name()
    {
        Touch("b.jar.disabled");

        var result = PackFileCollector.Collect(Array.Empty<PackFileEntry>(), ModsDirectory);

        var entry = Assert.Single(result);
        Assert.Equal("b.jar", entry.FileName);
    }

    [Fact]
    public void Index_entry_with_disabled_name_is_deduplicated()
    {
        Touch("c.jar.disabled");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("c.jar.disabled", "abc") },
            ModsDirectory);

        var entry = Assert.Single(result);
        Assert.Equal("c.jar", entry.FileName);
    }

    [Fact]
    public void Missing_mods_directory_returns_index_only()
    {
        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("x.jar", "abc") },
            Path.Combine(_root, "does-not-exist"));

        var entry = Assert.Single(result);
        Assert.Equal("x.jar", entry.FileName);
    }
}
