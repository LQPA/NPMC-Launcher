using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Skins;

/// <summary>
/// 离线皮肤 javaagent 的启动参数与部署。
///
/// 四个 agent jar（加载器位置各不相同，见 tools\skin-agent\build.ps1）：
///   - nmpc-skin-agent.jar：<c>-javaagent</c> 的薄入口（AgentStub），**不含 ASM**，两套实现共用。
///     JVM 会把这个 jar 放进系统类加载器，Fabric 的 <c>verifyClasspath</c> 能看到里面
///     的 <c>org/objectweb/asm</c>；一旦带 ASM 就与 Fabric 自带的 asm 重复，直接拒绝启动
///     （1.15 与 1.21.11 的 Fabric 都踩过）。所以它只负责用子加载器加载 impl jar。
///   - nmpc-skin-agent-impl.jar（Java 17+，含 ASM）：authlib 4~7，1.16~1.20.1 / 1.21.6 / 26.1.2；
///   - nmpc-skin-agent-impl-legacy.jar（Java 8，含 ASM）：authlib 1~3，1.7.10~1.15.x，只替换老的 getTextures；
///   - nmpc-skin-bridge.jar：被改写的游戏代码调用的桥接类。Java 17+ 追加到 bootstrap，
///     Java 8 追加到系统类加载器（LaunchWrapper / Fabric 的父加载器）。
///
/// agent 会：起本地皮肤 API、只把 session host 指到本地（4~7）、
/// 替换取皮肤入口返回本地 PNG，绕开 Mojang 签名校验。
/// 只加 JVM 参数，不动实例目录、不装模组。
/// </summary>
public static class SkinAgentProvider
{
    private const string StubResource = "NPMC.Launcher.Infrastructure.SkinAgent.jar";
    private const string ImplResource = "NPMC.Launcher.Infrastructure.SkinAgent.Impl.jar";
    private const string LegacyImplResource = "NPMC.Launcher.Infrastructure.SkinAgent.Impl.Legacy.jar";
    private const string BridgeResource = "NPMC.Launcher.Infrastructure.SkinAgentBridge.jar";

    private static readonly Lazy<byte[]> StubBytes = new(() => ReadResource(StubResource));
    private static readonly Lazy<byte[]> ImplBytes = new(() => ReadResource(ImplResource));
    private static readonly Lazy<byte[]> LegacyImplBytes = new(() => ReadResource(LegacyImplResource));
    private static readonly Lazy<byte[]> BridgeBytes = new(() => ReadResource(BridgeResource));

    /// <summary>agent 的启动参数 + 本地端口（SkinPort 的 CustomServer2 要用端口）。</summary>
    public sealed record SkinAgentLaunch(string Argument, int Port);

    /// <summary>给启动计划准备 <c>-javaagent:</c> 参数；不适用时返回 null。</summary>
    public static string? TryCreateArgument(
        LauncherPaths launcher, VersionJson version, PlayerAccount? account, string? clientJarPath)
        => TryCreateLaunch(launcher, version, account, clientJarPath)?.Argument;

    public static SkinAgentLaunch? TryCreateLaunch(
        LauncherPaths launcher, VersionJson version, PlayerAccount? account, string? clientJarPath)
    {
        if (account is null) return null;
        // 正版账号的皮肤由微软档案提供，不能再用离线 agent 覆盖
        if (account.Kind == AccountKind.Microsoft) return null;
        if (!SupportsSkinAgent(version)) return null;

        var major = AuthlibMajor(version) ?? 0;
        var modern = major >= 4;

        var resolved = account.Skin switch
        {
            AccountSkin.Steve => ExtractDefaultSkin(launcher, clientJarPath, "wide", "steve", "classic"),
            AccountSkin.Alex => ExtractDefaultSkin(launcher, clientJarPath, "slim", "alex", "slim"),
            _ => account.SkinPath is { Length: > 0 } path && File.Exists(path)
                ? (path, string.Equals(account.SkinModel, "slim", StringComparison.OrdinalIgnoreCase)
                    ? "slim"
                    : "classic")
                : ((string, string)?)null,
        };

        if (resolved is not { } skin) return null;

        var agent = EnsureAgentJars(launcher, modern);
        if (agent is not { } jars) return null;

        var bridge = EnsureBridgeJar(launcher);
        if (bridge is null) return null;

        var port = FindFreePort();
        if (port is null) return null;

        var logDirectory = Path.Combine(launcher.Root, "logs");
        Directory.CreateDirectory(logDirectory);

        var log = Path.Combine(logDirectory, "skin-agent.log");

        // impl jar 由 AgentStub 用子加载器加载，路径必须显式传进去
        // （Java 8 用 impl-legacy，Java 17+ 用 impl）。
        var settings = new List<string> { $"impl={jars.Impl}" };
        settings.Add($"port={port}");
        settings.Add($"name={account.Username}");
        settings.Add($"uuid={account.Uuid}");
        settings.Add($"skin={skin.Item1}");
        settings.Add($"model={skin.Item2}");
        settings.Add($"log={log}");
        settings.Add($"bridge={bridge}");

        return new SkinAgentLaunch($"-javaagent:{jars.Entry}=" + string.Join(";", settings), port.Value);
    }

    /// <summary>
    /// agent 支持的 authlib 主版本：1~10。
    /// 1~4 走 getTextures；5~9 走 unpackTextures（9 仍是 Yggdrasil*）；
    /// 10（26.3）换成 MinecraftServicesSessionService，靠 getPackedTextures 合成 textures 属性。
    /// </summary>
    public static bool SupportsSkinAgent(VersionJson version)
    {
        var major = AuthlibMajor(version);
        return major is >= 1 and <= 10;
    }

    /// <summary>authlib 4~7 会读 minecraft.api.*.host；更老/更新的版本不读。</summary>
    public static bool SupportsSessionHostOverride(VersionJson version)
        => AuthlibMajor(version) is >= 4 and <= 7;

    /// <summary>从版本 libraries 里取 com.mojang:authlib 的主版本号。</summary>
    public static int? AuthlibMajor(VersionJson version)
    {
        foreach (var library in version.Libraries)
        {
            var name = library.Name;
            if (!name.StartsWith("com.mojang:authlib:", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = name.Split(':');
            if (parts.Length < 3) return null;

            var versionText = parts[2];
            var dot = versionText.IndexOf('.');
            var majorText = dot > 0 ? versionText[..dot] : versionText;

            return int.TryParse(majorText, out var major) ? major : null;
        }

        return null;
    }

    /// <summary>从客户端 jar 的 assets 里取出官方默认皮肤（Steve=wide，Alex=slim）。</summary>
    private static (string Path, string Model)? ExtractDefaultSkin(
        LauncherPaths launcher, string? clientJarPath, string folder, string name, string model)
    {
        if (clientJarPath is null || !File.Exists(clientJarPath)) return null;

        var directory = Path.Combine(launcher.Root, "cache", "skins");
        var destination = Path.Combine(directory, $"default-{folder}-{name}.png");

        if (File.Exists(destination)) return (destination, model);

        try
        {
            Directory.CreateDirectory(directory);

            using var archive = ZipFile.OpenRead(clientJarPath);

            // 1.7.10 的默认皮肤在 entity/steve.png（64x32），1.8+ 才在 player/... 下
            var candidates = new List<string>
            {
                $"assets/minecraft/textures/entity/player/{folder}/{name}.png",
                $"assets/minecraft/textures/entity/player/wide/{name}.png",
                $"assets/minecraft/textures/entity/player/slim/{name}.png",
                $"assets/minecraft/textures/entity/{name}.png",
            };

            foreach (var candidate in candidates)
            {
                var entry = archive.GetEntry(candidate);
                if (entry is null) continue;

                entry.ExtractToFile(destination, overwrite: true);
                return File.Exists(destination) ? (destination, model) : null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 agent 需要的 jar 落到 <c>cache\agent</c>。
    /// 返回 (stub 入口, impl)；impl 按 Java 版本选 modern / legacy。
    /// </summary>
    private static (string Entry, string Impl)? EnsureAgentJars(LauncherPaths launcher, bool modern)
    {
        var directory = Path.Combine(launcher.Root, "cache", "agent");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            return null;
        }

        var stub = WriteAgentFile(directory, "nmpc-skin-agent.jar", StubBytes.Value);
        if (stub is null) return null;

        var impl = modern
            ? WriteAgentFile(directory, "nmpc-skin-agent-impl.jar", ImplBytes.Value)
            : WriteAgentFile(directory, "nmpc-skin-agent-impl-legacy.jar", LegacyImplBytes.Value);

        return impl is null ? null : (stub, impl);
    }

    /// <summary>
    /// 桥接 jar：只有它会被追加到 bootstrap，供模块化加载器解析。
    /// 它的类由被改写的游戏代码直接调用，必须落在 bootstrap；ASM 则留在 impl 的子加载器里。
    /// </summary>
    private static string? EnsureBridgeJar(LauncherPaths launcher)
    {
        var directory = Path.Combine(launcher.Root, "cache", "agent");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            return null;
        }

        return WriteAgentFile(directory, "nmpc-skin-bridge.jar", BridgeBytes.Value);
    }

    private static string? WriteAgentFile(string directory, string name, byte[] bytes)
    {
        if (bytes.Length == 0) return null;

        var path = Path.Combine(directory, name);

        try
        {
            // 直接覆盖：只比较长度时，改了实现但大小相同的 jar 会被留下来，
            // 表现就是"皮肤还是上一次缓存的那张"。
            File.WriteAllBytes(path, bytes);
        }
        catch
        {
            return null;
        }

        return path;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return Array.Empty<byte>();

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static int? FindFreePort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            return null;
        }
    }
}