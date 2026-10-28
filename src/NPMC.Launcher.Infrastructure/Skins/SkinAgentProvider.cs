using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Skins;

/// <summary>
/// 离线皮肤 javaagent 的启动参数与部署。
///
/// 两个 jar：
///   - modern（Java 17+）：authlib 4~7，1.16~1.20.1 / 1.21.6 / 26.1.2；
///   - legacy（Java 8）：authlib 1~3，1.7.10~1.15.x，只替换老的 getTextures。
///
/// agent 会：起本地皮肤 API、只把 session host 指到本地（4~7）、
/// 替换取皮肤入口返回本地 PNG，绕开 Mojang 签名校验。
/// 只加 JVM 参数，不动实例目录、不装模组。
/// </summary>
public static class SkinAgentProvider
{
    private const string ModernResource = "NPMC.Launcher.Infrastructure.SkinAgent.jar";
    private const string LegacyResource = "NPMC.Launcher.Infrastructure.SkinAgent.Legacy.jar";

    private static readonly Lazy<byte[]> ModernBytes = new(() => ReadResource(ModernResource));
    private static readonly Lazy<byte[]> LegacyBytes = new(() => ReadResource(LegacyResource));

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

        // 1.16+ 的模组加载器（NeoForge / Forge / Fabric）用模块化类加载器加载 authlib，
        // agent 注入的 nmpc.skin.* 只挂在系统类路径上，模块里看不到 —— 表现就是启动后在
        // SkinManager 取皮肤时 NoClassDefFoundError: nmpc/skin/SkinBridgeModern（ATM10 实测崩溃）。
        // 1.7.10/1.12 那代加载器还是普通 URLClassLoader，agent 正常工作，所以只排除 modern。
        if (modern && LoaderDetector.Detect(version).Count > 0) return null;

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

        var jar = EnsureAgentJar(launcher, modern);
        if (jar is null) return null;

        var port = FindFreePort();
        if (port is null) return null;

        var logDirectory = Path.Combine(launcher.Root, "logs");
        Directory.CreateDirectory(logDirectory);

        var log = Path.Combine(logDirectory, "skin-agent.log");

        var argument = $"-javaagent:{jar}=port={port};name={account.Username};uuid={account.Uuid}"
                       + $";skin={skin.Item1};model={skin.Item2};log={log}";

        return new SkinAgentLaunch(argument, port.Value);
    }

    /// <summary>agent 支持的 authlib 主版本：1~7。</summary>
    public static bool SupportsSkinAgent(VersionJson version)
    {
        var major = AuthlibMajor(version);
        return major is >= 1 and <= 7;
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

    private static string? EnsureAgentJar(LauncherPaths launcher, bool modern)
    {
        var bytes = modern ? ModernBytes.Value : LegacyBytes.Value;
        if (bytes.Length == 0) return null;

        var directory = Path.Combine(launcher.Root, "cache", "agent");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, modern ? "nmpc-skin-agent.jar" : "nmpc-skin-agent-legacy.jar");

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