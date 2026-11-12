using System.Text.Json;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Core.Paths;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 四家加载器的版本名解析。
/// 用例全部取自 <c>E:\aaaa\minecraft\instances</c> 里 38 个实例的真实 VersionId。
/// </summary>
public class LoaderVersionNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmpc-lvn-" + Guid.NewGuid().ToString("N"));

    public LoaderVersionNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private LauncherPaths MakePaths() => new(_root);

    private void WriteVersion(string id, string? inheritsFrom)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);

        var json = inheritsFrom is null
            ? JsonSerializer.Serialize(new { id, type = "release" })
            : JsonSerializer.Serialize(new { id, type = "release", inheritsFrom });

        File.WriteAllText(Path.Combine(dir, id + ".json"), json);
    }

    // ---- 加载器识别：四家分别匹配 ----

    [Theory]
    [InlineData("fabric-loader-0.18.4-1.21.11", "fabric", "0.18.4")]
    [InlineData("fabric-loader-0.20.1-1.15", "fabric", "0.20.1")]
    [InlineData("quilt-loader-0.30.1-26.3", "quilt", "0.30.1")]
    [InlineData("quilt-loader-0.31.0-beta.4-1.21.1", "quilt", "0.31.0-beta.4")]
    [InlineData("1.20.1-forge-47.1.106", "forge", "47.1.106")]
    [InlineData("26.3-forge-66.1.0", "forge", "66.1.0")]
    [InlineData("1.12.2-forge-14.23.5.2864", "forge", "14.23.5.2864")]
    [InlineData("1.7.10-Forge10.13.4.1614-1.7.10", "forge", "10.13.4.1614")]
    [InlineData("neoforge-21.1.259", "neoforge", "21.1.259")]
    [InlineData("neoforge-26.2.0.93", "neoforge", "26.2.0.93")]
    [InlineData("26.3", "vanilla", "")]
    [InlineData("1.21.11", "vanilla", "")]
    public void DetectLoader_matches_each_family(string versionId, string key, string version)
    {
        var info = LoaderVersionName.DetectLoader(versionId);
        Assert.Equal(key, info.LoaderKey);
        Assert.Equal(version, info.LoaderVersion);
    }

    // ---- 名字兜底：各家的 MC 版本位置不同 ----

    [Theory]
    [InlineData("26.3", "26.3")]                              // 原版
    [InlineData("26.4-snapshot-6", "26.4")]
    [InlineData("fabric-loader-0.18.4-1.21.11", "1.21.11")]   // 加载器在前
    [InlineData("quilt-loader-0.30.1-26.3", "26.3")]
    [InlineData("1.20.1-forge-47.1.106", "1.20.1")]           // MC 在最前
    [InlineData("26.3-forge-66.1.0", "26.3")]
    [InlineData("1.7.10-Forge10.13.4.1614-1.7.10", "1.7.10")]
    [InlineData("1.9-forge1.9-12.16.1.1938-1.9.0", "1.9")]
    [InlineData("neoforge-21.1.259", "")]                      // 名字里没有 MC 版本
    [InlineData("neoforge-26.1.2.124", "")]                    // 形似 MC，实为加载器版本
    [InlineData("22w13oneblockatatime", "")]
    public void GameVersionFromName_handles_each_family(string versionId, string expected)
        => Assert.Equal(expected, LoaderVersionName.GameVersionFromName(versionId));

    // ---- 主路径：读 inheritsFrom（四家都能拿到）----

    [Fact]
    public void ReadGameVersion_follows_inheritsFrom()
    {
        var paths = MakePaths();
        WriteVersion("fabric-loader-0.18.4-1.21.11", "1.21.11");
        WriteVersion("1.21.11", null);

        Assert.Equal("1.21.11", LoaderVersionName.ReadGameVersion(paths, "fabric-loader-0.18.4-1.21.11"));
    }

    [Fact]
    public void ReadGameVersion_solves_neoforge_that_name_cannot()
    {
        // 名字解析对 neoforge-21.1.259 无解，但 inheritsFrom 有答案
        var paths = MakePaths();
        WriteVersion("neoforge-21.1.259", "1.21.1");
        WriteVersion("1.21.1", null);

        Assert.Equal("", LoaderVersionName.GameVersionFromName("neoforge-21.1.259"));
        Assert.Equal("1.21.1", LoaderVersionName.ReadGameVersion(paths, "neoforge-21.1.259"));
        Assert.Equal("1.21.1", LoaderVersionName.ResolveGameVersion(paths, "neoforge-21.1.259"));
    }

    [Fact]
    public void ReadGameVersion_handles_chained_inheritance()
    {
        // 加载器 profile 继承另一个 profile，再继承 MC 版本
        var paths = MakePaths();
        WriteVersion("quilt-loader-0.31.0-beta.4-1.21.1", "fabric-loader-0.20.1-1.21.1");
        WriteVersion("fabric-loader-0.20.1-1.21.1", "1.21.1");
        WriteVersion("1.21.1", null);

        Assert.Equal("1.21.1", LoaderVersionName.ReadGameVersion(paths, "quilt-loader-0.31.0-beta.4-1.21.1"));
    }

    [Fact]
    public void ReadGameVersion_returns_empty_when_json_missing()
    {
        var paths = MakePaths();
        Assert.Equal("", LoaderVersionName.ReadGameVersion(paths, "1.21.1"));
    }

    [Fact]
    public void ReadGameVersion_survives_inheritance_cycle()
    {
        var paths = MakePaths();
        WriteVersion("a", "b");
        WriteVersion("b", "a");

        // 不能死循环；走到上限后返回当前值
        var result = LoaderVersionName.ReadGameVersion(paths, "a");
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void ResolveGameVersion_falls_back_to_name_when_json_missing()
    {
        var paths = MakePaths();
        // JSON 不存在 → 退回名字解析
        Assert.Equal("1.21.11", LoaderVersionName.ResolveGameVersion(paths, "fabric-loader-0.18.4-1.21.11"));
    }

    [Theory]
    [InlineData("fabric", "Fabric")]
    [InlineData("neoforge", "NeoForge")]
    [InlineData("vanilla", "原版")]
    public void DisplayName_is_human_readable(string key, string expected)
        => Assert.Equal(expected, LoaderVersionName.DisplayName(key));
}
