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
    /// <summary>把当前账号的皮肤同步到 SkinPort 的 cachedImages；不适用时静默返回。</summary>
    public static void Provision(string? gameDirectory, PlayerAccount? account)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || account is null) return;
        if (account.Skin is not (AccountSkin.Custom or AccountSkin.MojangName)) return;
        if (string.IsNullOrWhiteSpace(account.SkinPath) || !File.Exists(account.SkinPath)) return;

        // 只有装了 SkinPort 的实例才需要；没有配置文件的实例不要凭空造目录。
        if (!File.Exists(Path.Combine(gameDirectory, "config", "skinport.cfg"))) return;

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
}
