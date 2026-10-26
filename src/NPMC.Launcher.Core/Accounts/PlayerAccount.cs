using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NPMC.Launcher.Core.Accounts;

/// <summary>账号类型。目前只有离线账号；微软 / authlib-injector 作为后续通道。</summary>
public enum AccountKind
{
    Offline = 0,

    /// <summary>微软正版登录（等 Azure 应用注册审批，暂未实现）。</summary>
    Microsoft = 1,

    /// <summary>第三方认证（authlib-injector / 皮肤站），暂未实现。</summary>
    Authlib = 2,
}

/// <summary>
/// 账号的皮肤来源：内置 Steve / Alex、自定义 PNG、或按同名正版账号取。
/// </summary>
public enum AccountSkin
{
    Steve = 0,
    Alex = 1,

    /// <summary>自定义皮肤文件（PNG），手臂粗细单独选。</summary>
    Custom = 2,

    /// <summary>使用同名正版账号的皮肤（经 Mojang 皮肤 API 取回并缓存）。</summary>
    MojangName = 3,
}

/// <summary>
/// 一个玩家账号。离线账号只依赖用户名：UUID 按 Minecraft 的离线规则
/// （<c>UUID.nameUUIDFromBytes("OfflinePlayer:" + name)</c>，MD5 + version 3）算出来，
/// 这样在离线服务器上也能保持稳定的身份。
/// </summary>
public sealed record PlayerAccount(
    string Id,
    string Username,
    string Uuid,
    AccountKind Kind,
    DateTimeOffset CreatedAt,
    AccountSkin Skin = AccountSkin.Steve,
    string? SkinPath = null,
    string? SkinModel = null)
{
    /// <summary>名称以外的展示用信息。</summary>
    [JsonIgnore]
    public string KindText => Kind switch
    {
        AccountKind.Microsoft => "微软正版",
        AccountKind.Authlib => "第三方认证",
        _ => "离线",
    };

    [JsonIgnore]
    public string UuidDisplay => Uuid.Length == 32
        ? $"{Uuid[..8]}-{Uuid[8..12]}-{Uuid[12..16]}-{Uuid[16..20]}-{Uuid[20..]}"
        : Uuid;

    [JsonIgnore]
    public string SkinText => Skin switch
    {
        AccountSkin.Alex => "Alex",
        AccountSkin.Custom => "自定义",
        AccountSkin.MojangName => "同名正版",
        _ => "Steve",
    };

    public static bool IsValidUsername(string? username) =>
        !string.IsNullOrWhiteSpace(username)
        && username.Trim().Length is >= 1 and <= 16
        && username.Trim().All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    public static PlayerAccount CreateOffline(string username)
    {
        var name = username.Trim();

        if (!IsValidUsername(name))
            throw new ArgumentException("用户名只能是 1~16 个字母、数字或下划线。", nameof(username));

        return new PlayerAccount(
            Guid.NewGuid().ToString("N"),
            name,
            OfflineUuid(name),
            AccountKind.Offline,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 离线 UUID：MD5("OfflinePlayer:" + name)，然后按 UUID v3 的规则改两个字节。
    /// 与 Java 的 <c>UUID.nameUUIDFromBytes</c> 一致 —— 服务器用同一个名字会认出同一个玩家。
    /// </summary>
    public static string OfflineUuid(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));

        // version = 3，variant = RFC 4122
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
