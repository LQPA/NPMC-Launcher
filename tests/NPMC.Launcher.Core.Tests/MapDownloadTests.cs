using System.IO.Compression;
using NPMC.Launcher.Core.Worlds;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>地图版本匹配：宽松匹配（按主次版本段归类）。</summary>
public class MapVersionMatchTests
{
    [Fact]
    public void Exact_version_is_exact()
    {
        var level = MapVersionMatch.Match(new[] { "1.20.1", "1.20" }, "1.20.1");
        Assert.Equal(MapMatchLevel.Exact, level);
    }

    [Fact]
    public void Same_series_is_loose()
    {
        // 地图声明 26.1，实例是 26.1.2 —— 用户选定的宽松规则下应可匹配
        var level = MapVersionMatch.Match(new[] { "26.1" }, "26.1.2");
        Assert.Equal(MapMatchLevel.SameSeries, level);
    }

    [Fact]
    public void Different_series_does_not_match()
    {
        var level = MapVersionMatch.Match(new[] { "1.20.1" }, "1.21.1");
        Assert.Equal(MapMatchLevel.None, level);
    }

    [Fact]
    public void Snapshot_suffix_shares_series()
    {
        // 地图声明 26.3-snapshot：去掉后缀后与正式版 26.3 相等，按完全匹配处理
        Assert.Equal(MapMatchLevel.Exact, MapVersionMatch.Match(new[] { "26.3-snapshot" }, "26.3"));

        // 反过来也成立（实例是快照名、地图写正式版）
        Assert.Equal(MapMatchLevel.Exact, MapVersionMatch.Match(new[] { "26.3" }, "26.3-snapshot"));

        // 同段但不同补丁号才是宽松匹配
        Assert.Equal(MapMatchLevel.SameSeries,
            MapVersionMatch.Match(new[] { "26.4-snapshot" }, "26.4.1"));
    }

    [Fact]
    public void Exact_wins_over_same_series()
    {
        // 同时声明了 26.2 与 26.2.1：实例 26.2.1 应报告 Exact 而不是 SameSeries
        Assert.Equal(MapMatchLevel.Exact, MapVersionMatch.Match(new[] { "26.2", "26.2.1" }, "26.2.1"));
    }

    [Fact]
    public void Empty_inputs_do_not_match()
    {
        Assert.Equal(MapMatchLevel.None, MapVersionMatch.Match(Array.Empty<string>(), "1.20.1"));
        Assert.Equal(MapMatchLevel.None, MapVersionMatch.Match(new[] { "1.20.1" }, null));
        Assert.Equal(MapMatchLevel.None, MapVersionMatch.Match(new[] { "1.20.1" }, "  "));
    }

    [Theory]
    [InlineData("1.20.1", "1.20")]
    [InlineData("26.1.2", "26.1")]
    [InlineData("26.3-snapshot", "26.3")]
    [InlineData("1.8.9", "1.8")]
    [InlineData("26", "26")]
    [InlineData("", "")]
    public void SeriesOf_extracts_major_minor(string version, string expected)
        => Assert.Equal(expected, MapVersionMatch.SeriesOf(version));

    [Fact]
    public void DescribeMatch_lists_series_once()
    {
        var text = MapVersionMatch.DescribeMatch(new[] { "26.1", "26.1.1", "26.1.2" });
        Assert.Equal("支持 26.1", text);

        var many = MapVersionMatch.DescribeMatch(new[] { "26.1", "26.2", "26.3-snapshot" });
        Assert.Contains("26.1", many);
        Assert.Contains("26.3", many);
    }

    // ---- 实例 VersionId 的提取（E:\aaaa 里 38 个实例的真实取值）----

    [Theory]
    [InlineData("26.3", "26.3")]
    [InlineData("26.1.2", "26.1.2")]
    [InlineData("26.2", "26.2")]
    [InlineData("26.4-snapshot-6", "26.4")]
    [InlineData("1.21.1", "1.21.1")]
    [InlineData("1.21.11", "1.21.11")]
    [InlineData("1.20.1", "1.20.1")]
    [InlineData("1.12.2", "1.12.2")]
    [InlineData("1.7.10", "1.7.10")]
    [InlineData("1.9", "1.9")]
    [InlineData("1.15", "1.15")]
    [InlineData("fabric-loader-0.18.4-1.21.11", "1.21.11")]
    [InlineData("fabric-loader-0.20.1-1.15", "1.15")]
    [InlineData("quilt-loader-0.31.0-beta.4-1.21.1", "1.21.1")]
    [InlineData("quilt-loader-0.30.1-26.3", "26.3")]
    [InlineData("1.20.1-forge-47.1.106", "1.20.1")]
    [InlineData("26.3-forge-66.1.0", "26.3")]
    [InlineData("26.1.2-forge-64.1.5", "26.1.2")]
    [InlineData("1.21.1-forge-52.1.20", "1.21.1")]
    [InlineData("1.9-forge1.9-12.16.1.1938-1.9.0", "1.9")]
    [InlineData("1.7.10-Forge10.13.4.1614-1.7.10", "1.7.10")]
    [InlineData("1.12.2-forge-14.23.5.2864", "1.12.2")]
    [InlineData("neoforge-21.1.259", "")]           // 只有加载器版本
    [InlineData("neoforge-26.2.0.93", "")]          // 只有加载器版本（形似 MC）
    [InlineData("neoforge-26.1.2.124", "")]         // 加载器版本号长得跟 MC 一样，必须排除
    [InlineData("neoforge-21.11.48", "")]
    [InlineData("22w13oneblockatatime", "")]        // 愚人节快照，取不到
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MinecraftVersionOf_handles_real_version_ids(string? input, string expected)
        => Assert.Equal(expected, MapVersionMatch.MinecraftVersionOf(input));

    [Fact]
    public void Forge_style_instance_matches_by_minecraft_version()
    {
        // 修复前：整串 26.3-forge-66.1.0 被当版本号解析，结果不可靠
        Assert.Equal(MapMatchLevel.Exact, MapVersionMatch.Match(new[] { "26.3" }, "26.3-forge-66.1.0"));
        Assert.Equal(MapMatchLevel.Exact,
            MapVersionMatch.Match(new[] { "1.21.11" }, "fabric-loader-0.18.4-1.21.11"));

        // 同段但不同补丁号 → 宽松匹配
        Assert.Equal(MapMatchLevel.SameSeries,
            MapVersionMatch.Match(new[] { "26.1" }, "26.1.2-forge-64.1.5"));
    }

    [Fact]
    public void Instance_without_minecraft_version_never_matches()
    {
        // neoforge-21.1.259 若被当成 21.1，会错误匹配到 21.1 的地图
        Assert.Equal(MapMatchLevel.None, MapVersionMatch.Match(new[] { "21.1" }, "neoforge-21.1.259"));

        // neoforge-26.1.2.124 的加载器号形似 26.1.2，也不能匹配 26.1 的地图
        Assert.Equal(MapMatchLevel.None, MapVersionMatch.Match(new[] { "26.1.2" }, "neoforge-26.1.2.124"));
    }
}

/// <summary>地图解压：剥壳、防越界、不覆盖已有存档。</summary>
public class MapExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmpc-map-" + Guid.NewGuid().ToString("N"));

    public MapExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败无所谓 */ }
    }

    private string MakeZip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    private string Saves()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        return saves;
    }

    [Fact]
    public async Task Flat_archive_keeps_content_at_root()
    {
        var zip = MakeZip("flat.zip", ("level.dat", "x"), ("region/r.0.0.mca", "y"));

        var result = await MapExtractor.ExtractAsync(zip, Saves(), "Flat Map");

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(result.WorldDirectory, "level.dat")));
        Assert.True(File.Exists(Path.Combine(result.WorldDirectory, "region", "r.0.0.mca")));
        Assert.Equal(2, result.ExtractedFiles);
    }

    [Fact]
    public async Task Single_wrapper_directory_is_stripped()
    {
        var zip = MakeZip("wrapped.zip",
            ("MyMap/level.dat", "x"),
            ("MyMap/region/r.0.0.mca", "y"));

        var result = await MapExtractor.ExtractAsync(zip, Saves(), "Wrapped");

        Assert.True(File.Exists(Path.Combine(result.WorldDirectory, "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(result.WorldDirectory, "MyMap")));
    }

    [Fact]
    public async Task Double_wrapped_directory_is_stripped()
    {
        // MyMap/MyMap/level.dat —— Windows 右键压缩的典型产物
        var zip = MakeZip("double.zip", ("MyMap/MyMap/level.dat", "x"));

        var result = await MapExtractor.ExtractAsync(zip, Saves(), "Double");

        Assert.True(File.Exists(Path.Combine(result.WorldDirectory, "level.dat")));
    }

    [Fact]
    public async Task Zip_slip_is_refused()
    {
        var zip = MakeZip("evil.zip",
            ("level.dat", "x"),
            ("../escaped.txt", "should not escape"));

        var result = await MapExtractor.ExtractAsync(zip, Saves(), "Evil");

        // level.dat 正常落地，越界条目绝不能被写出
        Assert.True(File.Exists(Path.Combine(result.WorldDirectory, "level.dat")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")),
            "zip-slip 条目逃出了目标目录");

        // 越界条目要么被记进 Problems，要么被直接丢弃；两者都不能导致写出
        Assert.True(result.Problems.Count > 0 || result.ExtractedFiles == 1,
            $"越界条目既没报错也没被丢弃：Problems={result.Problems.Count}, Extracted={result.ExtractedFiles}");
    }

    [Fact]
    public async Task Existing_world_is_never_overwritten()
    {
        var saves = Saves();
        Directory.CreateDirectory(Path.Combine(saves, "Terralith"));
        File.WriteAllText(Path.Combine(saves, "Terralith", "level.dat"), "original");

        var zip = MakeZip("dup.zip", ("level.dat", "new"));
        var result = await MapExtractor.ExtractAsync(zip, saves, "Terralith");

        Assert.Equal("Terralith (2)", result.WorldName);
        Assert.Equal("original", File.ReadAllText(Path.Combine(saves, "Terralith", "level.dat")));
    }

    [Fact]
    public async Task Invalid_filename_characters_are_sanitized()
    {
        var zip = MakeZip("badname.zip", ("level.dat", "x"));

        var result = await MapExtractor.ExtractAsync(zip, Saves(), "Map: 1/2 <test>");

        Assert.DoesNotContain(':', result.WorldName);
        Assert.DoesNotContain('/', result.WorldName);
        Assert.DoesNotContain('<', result.WorldName);
    }

    [Fact]
    public async Task Multiple_worlds_in_one_archive_keep_their_folders()
    {
        var zip = MakeZip("multi.zip",
            ("MapA/level.dat", "a"),
            ("MapB/level.dat", "b"));

        var result = await MapExtractor.ExtractAsync(zip, Saves(), "Pack");

        Assert.True(Directory.Exists(Path.Combine(result.WorldDirectory, "MapA")));
        Assert.True(Directory.Exists(Path.Combine(result.WorldDirectory, "MapB")));
    }
}
