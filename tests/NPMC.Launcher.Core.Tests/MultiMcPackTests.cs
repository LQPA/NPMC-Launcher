using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Infrastructure.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// MultiMC / Prism / HMCL 实例包的导入。
///
/// 这类包与 mrpack / CurseForge 的根本区别：**文件全在包里**，安装等于解压；
/// 加载器版本必须严格按 mmc-pack.json，不允许"就近换版本"。
/// 实测样本：HMCL 导出的 1.21.11 + fabric-loader 0.18.4 包（mmc-pack.json 里
/// net.minecraft 出现了两次，解析必须容忍）。
/// </summary>
public class MultiMcPackTests
{
    [Fact]
    public void Detects_multimc_packs_and_keeps_other_formats_working()
    {
        var root = CreateTempRoot();
        try
        {
            var mmc = Path.Combine(root, "mmc.zip");
            WriteZip(mmc, new Dictionary<string, string>
            {
                ["mmc-pack.json"] = MmcJson("1.21.11", "net.fabricmc.fabric-loader", "0.18.4"),
                ["instance.cfg"] = InstanceCfg("测试包", 4096),
                [".minecraft/mods/a.jar"] = "x",
            });
            Assert.Equal(ModpackArchiveFormat.MultiMc, ModpackArchiveFormatDetector.Detect(mmc));

            var mrpack = Path.Combine(root, "pack.mrpack");
            WriteZip(mrpack, new Dictionary<string, string> { ["modrinth.index.json"] = "{}" });
            Assert.Equal(ModpackArchiveFormat.Modrinth, ModpackArchiveFormatDetector.Detect(mrpack));

            var cf = Path.Combine(root, "cf.zip");
            WriteZip(cf, new Dictionary<string, string> { ["manifest.json"] = "{}" });
            Assert.Equal(ModpackArchiveFormat.CurseForge, ModpackArchiveFormatDetector.Detect(cf));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Reads_game_version_and_strict_loader_version()
    {
        var root = CreateTempRoot();
        try
        {
            var artifact = Path.Combine(root, "pack.zip");
            WriteZip(artifact, new Dictionary<string, string>
            {
                // HMCL 实测把 net.minecraft 写了两遍
                ["mmc-pack.json"] = MmcJson("1.21.11", "net.fabricmc.fabric-loader", "0.18.4",
                    duplicateMinecraft: true),
                ["instance.cfg"] = InstanceCfg("Typing9923", 2048, width: 1600, height: 900),
                [".minecraft/mods/mod-a.jar"] = "a",
                [".minecraft/mods/mod-b.jar"] = "b",
                [".minecraft/config/x.txt"] = "c",
                [".minecraft/options.txt"] = "d",
                [".packignore"] = string.Empty,
            });

            var manifest = MultiMcPackManifest.TryRead(artifact);

            Assert.NotNull(manifest);
            Assert.Equal("1.21.11", manifest!.GameVersion);
            Assert.Equal("fabric-loader", manifest.LoaderName);
            Assert.Equal("0.18.4", manifest.LoaderVersion);
            Assert.Equal(".minecraft/", manifest.PayloadPrefix);
            Assert.Equal("Typing9923", manifest.Name);
            Assert.Equal(2048, manifest.MaxMemoryMb);
            Assert.Equal(1600, manifest.WindowWidth);
            Assert.Equal(900, manifest.WindowHeight);
            Assert.Equal(2, manifest.ModCount);
            // 载荷里不该出现包根的启动器元数据
            Assert.DoesNotContain(manifest.PayloadFiles, path => path.Contains("mmc-pack.json"));
            Assert.DoesNotContain(manifest.PayloadFiles, path => path.Contains(".packignore"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Supports_prism_style_minecraft_directory()
    {
        var root = CreateTempRoot();
        try
        {
            var artifact = Path.Combine(root, "prism.zip");
            WriteZip(artifact, new Dictionary<string, string>
            {
                ["mmc-pack.json"] = MmcJson("1.20.1", "net.minecraftforge", "47.2.0"),
                ["minecraft/mods/x.jar"] = "x",
            });

            var manifest = MultiMcPackManifest.TryRead(artifact);

            Assert.NotNull(manifest);
            Assert.Equal("minecraft/", manifest!.PayloadPrefix);
            Assert.Equal("forge", manifest.LoaderName);
            Assert.Equal("47.2.0", manifest.LoaderVersion);
            Assert.Equal(1, manifest.ModCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Maps_component_uids_to_loader_names()
    {
        Assert.Equal("fabric-loader", MultiMcPackManifest.MapLoaderUid("net.fabricmc.fabric-loader"));
        Assert.Equal("quilt-loader", MultiMcPackManifest.MapLoaderUid("org.quiltmc.quilt-loader"));
        Assert.Equal("neoforge", MultiMcPackManifest.MapLoaderUid("net.neoforged"));
        Assert.Equal("forge", MultiMcPackManifest.MapLoaderUid("net.minecraftforge"));
        Assert.Null(MultiMcPackManifest.MapLoaderUid("org.lwjgl"));
    }

    [Fact]
    public void Rejects_packs_without_a_minecraft_component()
    {
        var json = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            components = new object[] { new { uid = "net.fabricmc.fabric-loader", version = "0.18.4" } },
        });

        var manifest = MultiMcPackManifest.Parse("pack.zip", json, null, Array.Empty<string>());
        Assert.Null(manifest);
    }

    [Fact]
    public void Installer_extracts_payload_and_skips_launcher_metadata()
    {
        var root = CreateTempRoot();
        try
        {
            var artifact = Path.Combine(root, "pack.zip");
            WriteZip(artifact, new Dictionary<string, string>
            {
                ["mmc-pack.json"] = MmcJson("1.21.11", "net.fabricmc.fabric-loader", "0.18.4"),
                ["instance.cfg"] = InstanceCfg("测试包", 2048),
                [".minecraft/mods/a.jar"] = "AAA",
                [".minecraft/config/sub/b.cfg"] = "BBB",
                [".packignore"] = string.Empty,
            });

            var manifest = MultiMcPackManifest.TryRead(artifact)!;
            var plan = new ModpackPlan(
                manifest.Name,
                manifest.VersionLabel,
                new ModpackIndex(manifest.Name, "v1", manifest.GameVersion,
                    manifest.LoaderName, manifest.LoaderVersion, Array.Empty<ModpackFile>()),
                artifact,
                OverridesRoot: string.Empty,
                Problems: Array.Empty<string>())
            {
                PackedPayload = new PackedPayload(manifest.PayloadPrefix),
            };

            var target = Path.Combine(root, "instance");
            var result = ModpackInstaller
                .ExtractPackedPayloadAsync(plan, plan.PackedPayload!, target, null, default)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(2, result.OverrideFiles);
            Assert.Empty(result.Failures);
            Assert.True(File.Exists(Path.Combine(target, "mods", "a.jar")));
            Assert.True(File.Exists(Path.Combine(target, "config", "sub", "b.cfg")));
            Assert.False(File.Exists(Path.Combine(target, "mmc-pack.json")));
            Assert.False(File.Exists(Path.Combine(target, "instance.cfg")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Installer_refuses_entries_that_escape_the_instance_directory()
    {
        var root = CreateTempRoot();
        try
        {
            var artifact = Path.Combine(root, "evil.zip");
            WriteZip(artifact, new Dictionary<string, string>
            {
                ["mmc-pack.json"] = MmcJson("1.21.11", "net.fabricmc.fabric-loader", "0.18.4"),
                [".minecraft/../../evil.txt"] = "bad",
            });

            var manifest = MultiMcPackManifest.TryRead(artifact)!;
            var plan = new ModpackPlan(
                manifest.Name, "v1",
                new ModpackIndex(manifest.Name, "v1", manifest.GameVersion, manifest.LoaderName,
                    manifest.LoaderVersion, Array.Empty<ModpackFile>()),
                artifact, string.Empty, Array.Empty<string>())
            {
                PackedPayload = new PackedPayload(manifest.PayloadPrefix),
            };

            var target = Path.Combine(root, "instance");
            var result = ModpackInstaller
                .ExtractPackedPayloadAsync(plan, plan.PackedPayload!, target, null, default)
                .GetAwaiter()
                .GetResult();

            Assert.Single(result.Failures);
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string MmcJson(string gameVersion, string? loaderUid, string? loaderVersion, bool duplicateMinecraft = false)
    {
        var components = new List<object> { new { uid = "net.minecraft", version = gameVersion } };
        if (duplicateMinecraft)
        {
            components.Add(new { uid = "net.minecraft", version = gameVersion });
        }

        if (loaderUid is not null)
        {
            components.Add(new { uid = loaderUid, version = loaderVersion });
        }

        return JsonSerializer.Serialize(new { formatVersion = 1, components });
    }

    private static string InstanceCfg(string name, int maxMemory, int width = 854, int height = 480)
        => new StringBuilder()
            .AppendLine("#Auto generated by Hello Minecraft! Launcher")
            .AppendLine("InstanceType=OneSix")
            .AppendLine($"name={name}")
            .AppendLine($"MaxMemAlloc={maxMemory}")
            .AppendLine("MinMemAlloc=1024")
            .AppendLine($"MinecraftWinWidth={width}")
            .AppendLine($"MinecraftWinHeight={height}")
            .ToString();

    private static void WriteZip(string path, Dictionary<string, string> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-mmc-tests", Guid.NewGuid().ToString("N"));
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
