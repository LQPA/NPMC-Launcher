using System.Net;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.Infrastructure.Auth;

/// <summary>Microsoft 设备码流 + Xbox Live/XSTS + Minecraft Services 的最小实现。</summary>
public static class MicrosoftDeviceCodeAuth
{
    public const string DefaultScope = "XboxLive.signin offline_access";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record DeviceCode(
        string DeviceCodeValue,
        string UserCode,
        string VerificationUri,
        int ExpiresIn,
        int Interval,
        string Message);

    public sealed record Result(
        string MinecraftAccessToken,
        string MinecraftUuid,
        string MinecraftName,
        int EntitlementCount);

    public sealed record Tokens(string AccessToken, string RefreshToken, int ExpiresIn);

    public sealed record LoginResult(Result Minecraft, Tokens Microsoft);

    public sealed class AuthException : Exception
    {
        public AuthException(string stage, HttpStatusCode? status, string body)
            : base($"{stage} 失败" + (status is null ? "" : $"（HTTP {(int)status}）") + $": {Trim(body)}")
        {
            Stage = stage;
            Status = status;
            Body = body;
        }

        public string Stage { get; }
        public HttpStatusCode? Status { get; }
        public string Body { get; }
    }

    /// <summary>请求设备码。调用方负责把 user_code / verification_uri 展示给用户。</summary>
    public static async Task<DeviceCode> RequestDeviceCodeAsync(
        HttpClient http, string clientId, string tenant, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId 不能为空", nameof(clientId));

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = DefaultScope,
        });

        using var response = await http.PostAsync(DeviceCodeUrl(tenant), content, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AuthException("请求设备码", response.StatusCode, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return new DeviceCode(
            root.GetProperty("device_code").GetString() ?? "",
            root.GetProperty("user_code").GetString() ?? "",
            root.GetProperty("verification_uri").GetString() ?? "",
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900,
            root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
    }

    /// <summary>完整跑一遍：设备码 → 轮询 → XBL → XSTS → Minecraft → 所有权/档案。</summary>
    public static async Task<Result> RunAsync(
        HttpClient http,
        string clientId,
        string tenant,
        Action<DeviceCode>? onDeviceCode,
        CancellationToken cancellationToken = default)
        => (await LoginAsync(http, clientId, tenant, onDeviceCode, cancellationToken).ConfigureAwait(false))
            .Minecraft;

    /// <summary>登录并同时返回 refresh token；调用方负责加密保存。</summary>
    public static async Task<LoginResult> LoginAsync(
        HttpClient http,
        string clientId,
        string tenant,
        Action<DeviceCode>? onDeviceCode,
        CancellationToken cancellationToken = default)
    {
        var code = await RequestDeviceCodeAsync(http, clientId, tenant, cancellationToken)
            .ConfigureAwait(false);
        onDeviceCode?.Invoke(code);

        var microsoft = await WaitForTokenAsync(http, clientId, tenant, code, cancellationToken)
            .ConfigureAwait(false);

        var result = await CompleteAsync(http, microsoft.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        return new LoginResult(result, microsoft);
    }

    /// <summary>用 refresh token 换新的 Microsoft access/refresh token。</summary>
    public static async Task<Tokens> RefreshAsync(
        HttpClient http, string clientId, string tenant, string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = DefaultScope,
        });

        using var response = await http.PostAsync(TokenUrl(tenant), content, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AuthException("刷新令牌", response.StatusCode, body);

        return ParseTokens(body, response.StatusCode, refreshToken);
    }

    /// <summary>轮询设备码，返回 Microsoft token 对。</summary>
    public static async Task<Tokens> WaitForTokenAsync(
        HttpClient http, string clientId, string tenant, DeviceCode code, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, code.ExpiresIn));
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = code.DeviceCodeValue,
            });

            using var response = await http.PostAsync(TokenUrl(tenant), content, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return ParseTokens(body, response.StatusCode);

            var error = ReadError(body);
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new AuthException("轮询令牌", response.StatusCode, "设备码已过期，请重新运行");
                case "authorization_declined":
                    throw new AuthException("轮询令牌", response.StatusCode, "用户拒绝了授权");
                case "bad_verification_code":
                    throw new AuthException("轮询令牌", response.StatusCode, "设备码无效");
                default:
                    throw new AuthException("轮询令牌", response.StatusCode, body);
            }
        }

        throw new AuthException("轮询令牌", null, "等待授权超时");
    }

    /// <summary>用 Microsoft access token 换 Minecraft 令牌，并检查所有权/档案。</summary>
    public static async Task<Result> CompleteAsync(
        HttpClient http, string microsoftAccessToken, CancellationToken cancellationToken = default)
    {
        using var xbl = await PostJsonAsync(http, "https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, cancellationToken).ConfigureAwait(false);

        var xblToken = RequiredString(xbl, "Token", "XBL");
        var uhs = xbl.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                  ?? throw new AuthException("XBL", null, "响应缺少 uhs");

        using var xsts = await PostJsonAsync(http, "https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        }, cancellationToken).ConfigureAwait(false);

        var xstsToken = RequiredString(xsts, "Token", "XSTS");

        using var minecraft = await PostJsonAsync(
            http,
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" },
            cancellationToken).ConfigureAwait(false);

        var minecraftToken = RequiredString(minecraft, "access_token", "Minecraft 登录");

        using var entitlements = await GetJsonAsync(
            http, "https://api.minecraftservices.com/entitlements/mcstore", minecraftToken, cancellationToken)
            .ConfigureAwait(false);

        var entitlementCount = entitlements.RootElement.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
                ? items.GetArrayLength()
                : 0;

        using var profile = await GetJsonAsync(
            http, "https://api.minecraftservices.com/minecraft/profile", minecraftToken, cancellationToken)
            .ConfigureAwait(false);

        var name = RequiredString(profile.RootElement, "name", "档案");
        var uuid = RequiredString(profile.RootElement, "id", "档案");

        return new Result(minecraftToken, uuid, name, entitlementCount);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        HttpClient http, string url, object payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AuthException("请求 " + new Uri(url).Host, response.StatusCode, body);

        return JsonDocument.Parse(body);
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient http, string url, string bearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AuthException("请求 " + new Uri(url).Host, response.StatusCode, body);

        return JsonDocument.Parse(body);
    }

    private static string RequiredString(JsonDocument document, string property, string stage)
    {
        if (document.RootElement.TryGetProperty(property, out var value))
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        throw new AuthException(stage, null, $"响应缺少 {property}");
    }

    private static string RequiredString(JsonElement element, string property, string stage)
    {
        if (element.TryGetProperty(property, out var value))
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        throw new AuthException(stage, null, $"响应缺少 {property}");
    }

    private static Tokens ParseTokens(
        string body, HttpStatusCode status, string? previousRefresh = null)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        if (string.IsNullOrWhiteSpace(refresh)) refresh = previousRefresh;
        var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var value) ? value : 3600;

        if (string.IsNullOrWhiteSpace(access))
            throw new AuthException("获取令牌", status, "响应缺少 access_token");
        if (string.IsNullOrWhiteSpace(refresh))
            throw new AuthException("获取令牌", status, "响应缺少 refresh_token");

        return new Tokens(access, refresh, expires);
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string DeviceCodeUrl(string tenant) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/devicecode";

    private static string TokenUrl(string tenant) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token";

    private static string Trim(string value) =>
        value.Length <= 2000 ? value : value[..2000] + "...";
}
