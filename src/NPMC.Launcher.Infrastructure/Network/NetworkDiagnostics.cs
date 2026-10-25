using System.Net;
using System.Text.Json;

namespace NPMC.Launcher.Infrastructure.Network;

public sealed record NetworkResolution(
    string Host,
    IReadOnlyList<string> SystemAddresses,
    IReadOnlyList<string> PublicAddresses)
{
    public bool HasFakeIp => SystemAddresses.Any(NetworkDiagnostics.IsFakeIp);
}

/// <summary>
/// 网络路径诊断。
///
/// 一个必须澄清的概念：
///   **fake-IP 是 DNS 层的事，和路由规则是两回事。**
///   Clash/Surge 的 fake-IP 会把域名解析成 198.18.0.0/15 的假地址交给 TUN 接管；
///   即使该域名在规则里被设为 DIRECT，DNS 仍然返回假地址。
///   所以"解析到 198.18.x"只能说明 **TUN 接管了 DNS**，不能说明流量走了代理。
///
/// 要判断真实走向只有两条路：
///   1. 看实测延迟（比任何地址都可靠）
///   2. 看代理软件的连接日志（例如 Clash 的「连接」页，能看到每条连接命中的规则和出口）
///
/// 本类提供"系统解析 vs 公共 DoH 解析"的对照：前者是 fake-IP 视角，后者是真实 A 记录，
/// 两者放在一起能让人一眼看出本机 DNS 被接管了什么、真实地址是什么。
/// </summary>
public static class NetworkDiagnostics
{
    /// <summary>198.18.0.0/15 —— RFC 2544 保留段，被常见代理工具用作 fake-IP。</summary>
    public static bool IsFakeIp(string? address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;

        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    public static IReadOnlyList<string> ResolveSystem(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host).Select(address => address.ToString()).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>通过公共 DoH 拿真实 A 记录，绕过本机 DNS 的 fake-IP。</summary>
    public static async Task<IReadOnlyList<string>> ResolvePublicAsync(
        string host, HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("Answer", out var answers)) return Array.Empty<string>();

            return answers.EnumerateArray()
                .Where(answer => answer.TryGetProperty("type", out var type) && type.GetInt32() == 1)
                .Select(answer => answer.TryGetProperty("data", out var data) ? data.GetString() : null)
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
