using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Skins;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 离线皮肤 agent 的启用条件。
///
/// 1.16+ 的模组加载器用模块化类加载器加载 authlib，agent 注入的类在模块里看不到，
/// 表现是进游戏后取皮肤时 NoClassDefFoundError: nmpc/skin/SkinBridgeModern（ATM10 实测崩溃）。
/// </summary>
public class SkinAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "npmc-skin-agent-" + Guid.NewGuid().ToString("N"));

    private string SkinPath
    {
        get
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "skin.png");
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return path;
        }
    }

    private static VersionJson Version(string authlib, params string[] extraLibraries)
    {
        var libraries = new List<Library> { new() { Name = $"com.mojang:authlib:{authlib}" } };
        libraries.AddRange(extraLibraries.Select(name => new Library { Name = name }));

        return new VersionJson { Id = "test", Libraries = libraries };
    }

    private PlayerAccount OfflineAccount => new(
        "offline-1",
        "tester",
        "0123456789abcdef0123456789abcdef",
        AccountKind.Offline,
        DateTimeOffset.UnixEpoch,
        AccountSkin.Custom,
        SkinPath,
        "classic");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public void Modern_agent_is_used_for_modded_instances_too()
    {
        // 1.16+ 的模块化加载器靠 agent 的 bootstrap 追加 + read edge 解决，不再跳过
        var version = Version("6.0.54", "net.neoforged:neoforge:21.1.259");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);
    }

    [Fact]
    public void Modern_agent_is_used_for_forge_too()
    {
        var version = Version("4.0.43", "net.minecraftforge:forge:1.20.1-47.2.0");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);
    }

    [Fact]
    public void Modern_agent_is_used_for_fabric_too()
    {
        var version = Version("6.0.54", "net.fabricmc:fabric-loader:0.15.11");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);

        // Fabric 的 LoaderUtil.verifyClasspath 会数系统类加载器里的 ASM：
        // -javaagent 的 jar 本身就在系统类路径上，所以它不能带 ASM，实现必须走 impl 子加载器。
        Assert.Contains("impl=", launch!.Argument);
    }

    [Fact]
    public void Javaagent_entry_jar_carries_no_asm()
    {
        var version = Version("6.0.54", "net.fabricmc:fabric-loader:0.15.11");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);

        // 形如 -javaagent:<entry>=impl=<impl>;port=...
        var argument = launch!.Argument["-javaagent:".Length..];
        var entry = argument[..argument.IndexOf('=')];

        Assert.True(File.Exists(entry), entry);

        using var archive = System.IO.Compression.ZipFile.OpenRead(entry);

        Assert.DoesNotContain(
            archive.Entries,
            item => item.FullName.StartsWith("org/objectweb/asm/", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_agent_is_self_contained()
    {
        // 1.7.10 的 Forge 不做重复 ASM 检查，legacy jar 保持自包含（含桥接与 ASM），不传 impl
        var version = Version("1.5.3", "net.minecraftforge:forge:1.7.10-10.13.4.1614");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);
        Assert.DoesNotContain("impl=", launch!.Argument);
        Assert.Contains("nmpc-skin-agent-legacy.jar", launch.Argument);
    }

    [Fact]
    public void Keeps_modern_agent_for_vanilla_versions()
    {
        var version = Version("6.0.54");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);
        Assert.Contains("-javaagent:", launch!.Argument);
    }

    [Fact]
    public void Keeps_legacy_agent_for_old_forge()
    {
        // 1.7.10：authlib 1.x + Forge，加载器是普通 URLClassLoader，agent 可用
        var version = Version("1.5.3", "net.minecraftforge:forge:1.7.10-10.13.4.1614");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), version, OfflineAccount, clientJarPath: null);

        Assert.NotNull(launch);
    }

    [Fact]
    public void Microsoft_accounts_never_use_the_agent()
    {
        var account = new PlayerAccount(
            "ms-1", "tester", "0123456789abcdef0123456789abcdef", AccountKind.Microsoft,
            DateTimeOffset.UnixEpoch, AccountSkin.Custom, SkinPath, "classic");

        var launch = SkinAgentProvider.TryCreateLaunch(
            new LauncherPaths(_root), Version("6.0.54"), account, clientJarPath: null);

        Assert.Null(launch);
    }
}
