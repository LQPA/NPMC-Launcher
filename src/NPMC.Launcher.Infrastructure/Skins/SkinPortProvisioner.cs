using System.Text.RegularExpressions;
using NPMC.Launcher.Core.Accounts;

namespace NPMC.Launcher.Infrastructure.Skins;

/// <summary>
/// 1.7.10 整合包常见的 SkinPort（lain.mods.skinport）有自己的皮肤提供者链。
/// 它的 UserManagedSkinProvider 会优先读取：
///   &lt;gameDir&gt;\cachedImages\skins\&lt;用户名&gt;.png
///   &lt;gameDir&gt;\cachedImages\skins\uuid\&lt;UUID 去掉短横线&gt;.png
/// 离线账号在 Mojang / Crafatar 都查不到，最后会回落成 SkinPort 自带的 Alex/Steve。
///
/// 启动器在这里把账号选的自定义皮肤复制进 SkinPort 的本地目录，
/// 这样即使 jvm 的 authlib javaagent 插不进它的 provider 链，皮肤也能生效。
/// 只对真的装了 SkinPort（存在 config\skinport.cfg）的实例动手，其它实例不产生文件。
/// </summary>
public static class SkinPortProvisioner
{
    /// <summary>
    /// 把当前账号的皮肤同步到 SkinPort，并把它的 CustomServer2 指向 agent 的本地 HTTP 服务。
    /// 本地服务只回本账号；其它 UUID 返回 404，SkinPort 会继续走 Mojang/Crafatar。
    /// </summary>
    public static void Provision(string? gameDirectory, PlayerAccount? account, int? agentPort)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || account is null) return;

        // 只有装了 SkinPort 的实例才需要；没有配置文件的实例不要凭空造目录。
        var config = Path.Combine(gameDirectory, "config", "skinport.cfg");
        if (!File.Exists(config)) return;

        // 正版账号不能用之前离线时留下的本地皮肤；删掉同名缓存，让 SkinPort 走正版档案。
        if (account.Kind == AccountKind.Microsoft)
        {
            try
            {
                var skins = Path.Combine(gameDirectory, "cachedImages", "skins");
                File.Delete(Path.Combine(skins, account.Username + ".png"));
                File.Delete(Path.Combine(skins, "uuid", account.Uuid.Replace("-", "") + ".png"));
            }
            catch
            {
                // 删不掉就让 SkinPort 自己决定；不阻断启动
            }
        }

        var hasLocalSkin = account.Skin is AccountSkin.Custom or AccountSkin.MojangName
                           && !string.IsNullOrWhiteSpace(account.SkinPath)
                           && File.Exists(account.SkinPath);

        // 注意：这里**不**关闭 useMojang / useCrafatar。
        // 其它玩家的 UUID 还要靠它们去正版服务器验证；本地账号由下方的
        // CustomServer2（agent 的 /skins/%auto%）和 cachedImages 优先覆盖。
        try
        {
            var text = File.ReadAllText(config);
            var updated = SetBoolean(text, "useMojang", enabled: true);
            updated = SetBoolean(updated, "useCrafatar", enabled: true);

            if (hasLocalSkin && agentPort is > 0)
            {
                updated = SetBoolean(updated, "useCustomServer2", enabled: true);
                updated = SetString(updated, "hostCustomServer2Skin",
                    $"http://127.0.0.1:{agentPort}/skins/%auto%");
            }
            else if (!hasLocalSkin)
            {
                updated = SetBoolean(updated, "useCustomServer2", enabled: false);
            }

            if (!string.Equals(text, updated, StringComparison.Ordinal))
                File.WriteAllText(config, updated);
        }
        catch
        {
            // 配置改不了不影响本地皮肤文件；最多仍会发一次远端请求。
        }

        if (!hasLocalSkin) return;

        try
        {
            var skins = Path.Combine(gameDirectory, "cachedImages", "skins");
            Directory.CreateDirectory(skins);

            // 离线玩家走用户名；正版/其他玩家走 UUID 目录。
            var byName = Path.Combine(skins, account.Username + ".png");
            File.Copy(account.SkinPath, byName, overwrite: true);

            var uuidDirectory = Path.Combine(skins, "uuid");
            Directory.CreateDirectory(uuidDirectory);

            var byUuid = Path.Combine(uuidDirectory, account.Uuid.Replace("-", "") + ".png");
            File.Copy(account.SkinPath, byUuid, overwrite: true);
        }
        catch
        {
            // 皮肤缓存写不进去不该阻断启动；agent 仍会尝试 authlib 替换。
        }
    }

    /// <summary>
    /// 只改 SkinPort 配置里的一个布尔项，保留缩进和其余内容。
    /// 配置不存在该项时追加到 client 段末尾之前（Forge Configuration 的常见布局）。
    /// </summary>
    private static string SetBoolean(string text, string key, bool enabled)
    {
        var value = enabled ? "true" : "false";
        var pattern = $@"(?m)^(\s*B:{Regex.Escape(key)}\s*=\s*)(true|false)\s*$";

        if (Regex.IsMatch(text, pattern))
            return Regex.Replace(text, pattern, m => m.Groups[1].Value + value);

        return text;
    }

    private static string SetString(string text, string key, string value)
    {
        var pattern = $@"(?m)^(\s*S:{Regex.Escape(key)}\s*=\s*).*$";
        if (!Regex.IsMatch(text, pattern)) return text;

        return Regex.Replace(text, pattern, m => m.Groups[1].Value + value);
    }
}
