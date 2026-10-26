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
/// 做法：启动时把自带的 agent（已内含 ASM）解到 &lt;root&gt;\cache\agent\，
/// 再用 <c>-javaagent:...</c> 传账号与皮肤路径。agent 会：
///   - 在 127.0.0.1 起一个只有当前账号的皮肤 API；
///   - 只把 minecraft.api.session.host 指过去（auth/account/services 保持官方，在线功能不受影响）；
///   - 把 authlib 的 getTextures 换成直接返回本地皮肤，绕开 Mojang 签名校验。
///
/// 只对 authlib 4.x~7.x（大致 1.16~1.20.x）启用；更老的没有这些入口，
/// 更新的 authlib 10.x 已不再读这些属性，暂不处理。
/// </summary>
public static class SkinAgentProvider
{
    private const string ResourceName = "NPMC.Launcher.Infrastructure.SkinAgent.jar";

    private static readonly Lazy<byte[]> AgentBytes = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);

        if (stream is null) return Array.Empty<byte>();

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    /// <summary>给启动计划准备 <c>-javaagent:</c> 参数；不适用时返回 null。</summary>
    public static string? TryCreateArgument(
        LauncherPaths launcher, VersionJson version, PlayerAccount? account, string? clientJarPath)
    {
        if (account is null) return null;
        if (!SupportsSessionHostOverride(version)) return null;

        // Steve / Alex 从客户端 jar 里取官方默认皮肤；自定义 / 同名正版用账号里缓存的 PNG
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

        var jar = EnsureAgentJar(launcher);
        if (jar is null) return null;

        var port = FindFreePort();

        if (port is null) return null;

        var logDirectory = Path.Combine(launcher.Root, "logs");
        Directory.CreateDirectory(logDirectory);

        var log = Path.Combine(logDirectory, "skin-agent.log");

        return $"-javaagent:{jar}=port={port};name={account.Username};uuid={account.Uuid}"
               + $";skin={skin.Item1};model={skin.Item2};log={log}";
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

            using var archive = System.IO.Compression.ZipFile.OpenRead(clientJarPath);
            var entry = archive.GetEntry($"assets/minecraft/textures/entity/player/{folder}/{name}.png");

            if (entry is null) return null;

            entry.ExtractToFile(destination, overwrite: true);
            return File.Exists(destination) ? (destination, model) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>authlib 4.x~7.x 会读 minecraft.api.*.host；其它版本跳过。</summary>
    public static bool SupportsSessionHostOverride(VersionJson version)
    {
        foreach (var library in version.Libraries)
        {
            var name = library.Name;
            if (!name.StartsWith("com.mojang:authlib:", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = name.Split(':');
            if (parts.Length < 3) return false;

            var versionText = parts[2];
            var dot = versionText.IndexOf('.');
            var majorText = dot > 0 ? versionText[..dot] : versionText;

            return int.TryParse(majorText, out var major) && major is >= 4 and <= 7;
        }

        return false;
    }

    private static string? EnsureAgentJar(LauncherPaths launcher)
    {
        var bytes = AgentBytes.Value;
        if (bytes.Length == 0) return null;

        var directory = Path.Combine(launcher.Root, "cache", "agent");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "nmpc-skin-agent.jar");

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
