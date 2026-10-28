using NPMC.Launcher.Core.Instances;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 「整合包自带文件」清单：清单里的文件与 overrides/ 带进来的模组、资源包、光影都要算，
/// 且只有清单里的那些才允许打平台来源徽标（overrides 里的自制内容要保持 `?`）。
/// </summary>
public class PackFileCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "npmc-pack-files-" + Guid.NewGuid().ToString("N"));

    private string Directory_(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void Touch(string directory, string name) =>
        File.WriteAllText(Path.Combine(Directory_(directory), name), "x");

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
    public void Adds_override_files_that_are_missing_from_the_index()
    {
        Touch("mods", "from-index.jar");
        Touch("mods", "from-overrides.jar");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("from-index.jar", "abc") },
            _root);

        Assert.Equal(2, result.Count);

        var index = Assert.Single(result, entry => entry.FileName == "from-index.jar");
        Assert.True(index.FromIndex, "清单里的文件来自平台，应该打来源徽标");

        var added = Assert.Single(result, entry => entry.FileName == "from-overrides.jar");
        Assert.False(added.FromIndex, "overrides 带进来的文件平台不认识，徽标要保持 ?");
    }

    [Fact]
    public void Keeps_index_sha1_and_ignores_unrelated_files()
    {
        Touch("mods", "a.jar");
        Touch("mods", "readme.txt");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("a.jar", "deadbeef") },
            _root);

        var entry = Assert.Single(result);
        Assert.Equal("a.jar", entry.FileName);
        Assert.Equal("deadbeef", entry.Sha1);
    }

    [Fact]
    public void Disabled_files_are_recorded_under_their_logical_name()
    {
        Touch("mods", "b.jar.disabled");

        var result = PackFileCollector.Collect(Array.Empty<PackFileEntry>(), _root);

        var entry = Assert.Single(result);
        Assert.Equal("b.jar", entry.FileName);
        Assert.False(entry.FromIndex);
    }

    [Fact]
    public void Index_entry_with_disabled_name_is_deduplicated()
    {
        Touch("mods", "c.jar.disabled");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("c.jar.disabled", "abc") },
            _root);

        var entry = Assert.Single(result);
        Assert.Equal("c.jar", entry.FileName);
        Assert.True(entry.FromIndex);
    }

    [Fact]
    public void Resource_packs_and_shader_packs_are_collected_too()
    {
        Touch("resourcepacks", "pack.zip");
        Touch("shaderpacks", "shader.zip");
        Touch("shaderpacks", "disabled-shader.zip.disabled");

        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("pack.zip", "abc") },
            _root);

        Assert.Equal(3, result.Count);
        Assert.True(Assert.Single(result, entry => entry.FileName == "pack.zip").FromIndex);
        Assert.False(Assert.Single(result, entry => entry.FileName == "shader.zip").FromIndex);
        Assert.False(Assert.Single(result, entry => entry.FileName == "disabled-shader.zip").FromIndex);
    }

    [Fact]
    public void Missing_content_directories_return_index_only()
    {
        var result = PackFileCollector.Collect(
            new[] { new PackFileEntry("x.jar", "abc") },
            Path.Combine(_root, "does-not-exist"));

        var entry = Assert.Single(result);
        Assert.Equal("x.jar", entry.FileName);
        Assert.True(entry.FromIndex);
    }
}
