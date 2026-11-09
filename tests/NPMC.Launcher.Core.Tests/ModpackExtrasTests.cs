using System.IO.Compression;
using System.Text;
using NPMC.Launcher.Core.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 整合包预设参数（JVM 参数 / 一键进服地址）。
///
/// 这类参数是任意代码执行入口，所以解析要严格：标准字段优先（MultiMC 的 instance.cfg），
/// 我们的 .nmpc-pack.json 只补缺口；拆分要尊重引号。
/// </summary>
public class ModpackExtrasTests
{
    [Fact]
    public void Splits_arguments_respecting_quotes()
    {
        var parts = ModpackExtras.SplitArguments("-Xmx4G -Dfoo=\"bar baz\" -javaagent:agent.jar");

        Assert.Equal(new[] { "-Xmx4G", "-Dfoo=bar baz", "-javaagent:agent.jar" }, parts);
    }

    [Fact]
    public void Parses_our_pack_file()
    {
        var extras = ModpackExtras.Parse("""
        {
          "jvmArgs": ["-Xmx6G", "-XX:+UseG1GC"],
          "quickPlayServer": "play.example.com:25565"
        }
        """);

        Assert.Equal(new[] { "-Xmx6G", "-XX:+UseG1GC" }, extras.JvmArguments);
        Assert.Equal("play.example.com:25565", extras.QuickPlayServer);
    }

    [Fact]
    public void Accepts_jvm_args_as_a_single_string()
    {
        var extras = ModpackExtras.Parse("""{ "jvmArgs": "-Xmx4G -Dfoo=bar" }""");

        Assert.Equal(new[] { "-Xmx4G", "-Dfoo=bar" }, extras.JvmArguments);
        Assert.Null(extras.QuickPlayServer);
    }

    [Fact]
    public void Reads_extras_from_mmc_pack_and_standard_instance_cfg()
    {
        var root = CreateTempRoot();
        try
        {
            var artifact = Path.Combine(root, "pack.zip");
            WriteZip(artifact, new Dictionary<string, string>
            {
                ["mmc-pack.json"] = """{ "formatVersion": 1, "components": [ { "uid": "net.minecraft", "version": "1.21.1" } ] }""",
                ["instance.cfg"] = """
                name=带参数
                MaxMemAlloc=6144
                OverrideJavaArgs=true
                JvmArgs=-Xmx8G -Dfoo="bar baz"
                """,
                [".minecraft/options.txt"] = "quickPlayMultiplayer:play.example.com:25565\nfov:0.5",
                [".minecraft/mods/a.jar"] = "x",
            });

            var manifest = MultiMcPackManifest.TryRead(artifact);

            Assert.NotNull(manifest);
            Assert.Equal(new[] { "-Xmx8G", "-Dfoo=bar baz" }, manifest!.Extras.JvmArguments);
            Assert.Equal("play.example.com:25565", manifest.Extras.QuickPlayServer);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Ignores_jvm_args_when_override_is_false()
    {
        var root = CreateTempRoot();
        try
        {
            var artifact = Path.Combine(root, "pack.zip");
            WriteZip(artifact, new Dictionary<string, string>
            {
                ["mmc-pack.json"] = """{ "formatVersion": 1, "components": [ { "uid": "net.minecraft", "version": "1.20.1" } ] }""",
                ["instance.cfg"] = """
                name=不用参数
                OverrideJavaArgs=false
                JvmArgs=-Xmx8G
                """,
            });

            var manifest = MultiMcPackManifest.TryRead(artifact);

            Assert.NotNull(manifest);
            Assert.Empty(manifest!.Extras.JvmArguments);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Quick_play_arguments_pick_the_right_flavour()
    {
        Assert.Equal(
            new[] { "--quickPlayMultiplayer", "host:25566" },
            ModpackExtras.BuildQuickPlayArguments("host:25566", "1.21.1"));

        // 1.19 及更早没有 quickPlay，退回 --server/--port
        Assert.Equal(
            new[] { "--server", "host", "--port", "25566" },
            ModpackExtras.BuildQuickPlayArguments("host:25566", "1.19.2"));

        // 不写端口时按 25565
        Assert.Equal(
            new[] { "--quickPlayMultiplayer", "host:25565" },
            ModpackExtras.BuildQuickPlayArguments("host", "1.20.1"));
    }

    [Fact]
    public void Merges_jvm_arguments_dedupes_and_keeps_the_latter()
    {
        var merged = JvmArgumentMerger.Merge(
            new[] { "-Xmx4G", "-Dfoo=1", "-XX:+UseG1GC" },
            new[] { "-Xmx8G", "-Dfoo=1" });

        // 顺序保持原样（追加的覆盖项落在后面），但只保留一个 -Xmx 且是追加的那个
        Assert.DoesNotContain("-Xmx4G", merged.Arguments);
        Assert.Contains("-Xmx8G", merged.Arguments);
        Assert.Contains("-XX:+UseG1GC", merged.Arguments);
        Assert.Single(merged.Arguments, argument => argument.StartsWith("-Dfoo", StringComparison.Ordinal));
        Assert.Equal(1, merged.DuplicatesRemoved);
        Assert.Equal(1, merged.Overridden);
        Assert.Contains(merged.Notes, note => note.Contains("-Xmx8G"));
    }
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
        var root = Path.Combine(Path.GetTempPath(), "nmpc-extras-tests", Guid.NewGuid().ToString("N"));
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
