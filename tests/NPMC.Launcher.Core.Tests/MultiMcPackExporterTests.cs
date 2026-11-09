using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NPMC.Launcher.Infrastructure.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 导出 MultiMC 实例包（替代原来的「完整 ZIP」）。
///
/// 原来的完整 ZIP 谁也导不回来；MMC 包能被子启动器重新导入，Prism / HMCL / MultiMC 也认。
/// 这里验证：两个清单文件内容正确、载荷在 .minecraft/ 下、日志与存档的取舍。
/// </summary>
public class MultiMcPackExporterTests
{
    [Fact]
    public void Exports_manifests_and_payload()
    {
        var root = CreateTempRoot();
        try
        {
            var instanceDir = Path.Combine(root, "instance");
            Write(instanceDir, "mods/a.jar", 100);
            Write(instanceDir, "config/b.txt", 50);
            Write(instanceDir, "logs/latest.log", 10);
            Write(instanceDir, "crash-reports/crash.txt", 10);
            Write(instanceDir, "saves/world/level.dat", 20);
            Write(instanceDir, ".nmpc-modpack.json", 5);

            var target = Path.Combine(root, "pack.zip");
            var result = MultiMcPackExporter.Export(new MultiMcExportRequest(
                InstanceDirectory: instanceDir,
                TargetPath: target,
                Name: "测试实例",
                GameVersion: "1.21.11",
                LoaderKey: "fabric-loader",
                LoaderVersion: "0.18.4",
                MemoryMb: 8192,
                WindowWidth: 1600,
                WindowHeight: 900,
                IncludeSaves: false));

            // 只打模组与配置；logs / crash-reports / saves / 内部标记都不进包
            Assert.Equal(2, result.Files);

            using var archive = ZipFile.OpenRead(target);
            var names = archive.Entries.Select(entry => entry.FullName).ToList();

            Assert.Contains(".minecraft/mods/a.jar", names);
            Assert.Contains(".minecraft/config/b.txt", names);
            Assert.DoesNotContain(names, name => name.Contains("logs/"));
            Assert.DoesNotContain(names, name => name.Contains("crash-reports/"));
            Assert.DoesNotContain(names, name => name.Contains("saves/"));
            Assert.DoesNotContain(names, name => name.Contains(".nmpc-modpack.json"));

            // mmc-pack.json：游戏版本 + 加载器 uid/version
            var packJson = ReadEntry(archive, "mmc-pack.json");
            using var document = JsonDocument.Parse(packJson);
            var components = document.RootElement.GetProperty("components").EnumerateArray().ToList();

            Assert.Equal("net.minecraft", components[0].GetProperty("uid").GetString());
            Assert.Equal("1.21.11", components[0].GetProperty("version").GetString());
            Assert.Equal("net.fabricmc.fabric-loader", components[1].GetProperty("uid").GetString());
            Assert.Equal("0.18.4", components[1].GetProperty("version").GetString());

            // instance.cfg：名字 / 内存 / 窗口
            var cfg = ReadEntry(archive, "instance.cfg");
            Assert.Contains("name=测试实例", cfg);
            Assert.Contains("MaxMemAlloc=8192", cfg);
            Assert.Contains("MinecraftWinWidth=1600", cfg);
            Assert.Contains("MinecraftWinHeight=900", cfg);

            Assert.Contains(".packignore", names);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Saves_are_included_only_when_requested()
    {
        var root = CreateTempRoot();
        try
        {
            var instanceDir = Path.Combine(root, "instance");
            Write(instanceDir, "mods/a.jar", 10);
            Write(instanceDir, "saves/world/level.dat", 10);

            var target = Path.Combine(root, "pack-with-saves.zip");
            var result = MultiMcPackExporter.Export(new MultiMcExportRequest(
                instanceDir, target, "带存档", "1.20.1", null, null, 4096, 1280, 720, IncludeSaves: true));

            Assert.Equal(2, result.Files);

            using var archive = ZipFile.OpenRead(target);
            Assert.Contains(archive.Entries, entry => entry.FullName == ".minecraft/saves/world/level.dat");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Vanilla_instance_has_only_the_minecraft_component()
    {
        var request = new MultiMcExportRequest(
            "x", "y", "原版", "1.21.1", null, null, 4096, 1280, 720, IncludeSaves: false);

        using var document = JsonDocument.Parse(MultiMcPackExporter.BuildPackJson(request));
        Assert.Equal(1, document.RootElement.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public void Loader_keys_map_to_multimc_uids()
    {
        Assert.Equal("net.fabricmc.fabric-loader", MultiMcPackExporter.MapLoaderUid("fabric-loader"));
        Assert.Equal("org.quiltmc.quilt-loader", MultiMcPackExporter.MapLoaderUid("quilt-loader"));
        Assert.Equal("net.neoforged", MultiMcPackExporter.MapLoaderUid("neoforge"));
        Assert.Equal("net.minecraftforge", MultiMcPackExporter.MapLoaderUid("forge"));
        Assert.Null(MultiMcPackExporter.MapLoaderUid("liteloader"));
    }

    [Fact]
    public void Rounded_round_trip_through_manifest_parser()
    {
        var root = CreateTempRoot();
        try
        {
            var instanceDir = Path.Combine(root, "instance");
            Write(instanceDir, "mods/a.jar", 10);

            var target = Path.Combine(root, "round-trip.zip");
            MultiMcPackExporter.Export(new MultiMcExportRequest(
                instanceDir, target, "往返", "1.21.11", "fabric-loader", "0.18.4", 6144, 1280, 720,
                IncludeSaves: true, Notes: "第一行\n第二行"));

            // 用导入侧的解析器读回来：应能得到同样的版本与加载器
            var manifest = NPMC.Launcher.Core.Modpacks.MultiMcPackManifest.TryRead(target);

            Assert.NotNull(manifest);
            Assert.Equal("1.21.11", manifest!.GameVersion);
            Assert.Equal("fabric-loader", manifest.LoaderName);
            Assert.Equal("0.18.4", manifest.LoaderVersion);
            Assert.Equal(6144, manifest.MaxMemoryMb);
            Assert.Equal(".minecraft/", manifest.PayloadPrefix);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Write(string root, string relative, int bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', bytes)));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-mmc-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响结论
        }
    }
}
