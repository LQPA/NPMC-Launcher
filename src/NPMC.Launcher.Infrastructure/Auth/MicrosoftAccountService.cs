using System.Net.Http;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Accounts;

namespace NPMC.Launcher.Infrastructure.Auth;

/// <summary>
/// 微软账号的登录与启动令牌入口。
/// 登录只返回 refresh token 给调用方；由调用方用 DPAPI 保存。
/// </summary>
public sealed class MicrosoftAccountService
{
    /// <summary>公开的公共客户端 ID，可被 NPMC_MS_CLIENT_ID 覆盖。</summary>
    public const string DefaultClientId = "cb163b2f-828f-4355-b861-1341ee2153ce";

    private readonly MicrosoftTokenStore _tokens;
    private readonly string _clientId;
    private readonly string _tenant;

    public MicrosoftAccountService(
        LauncherPaths paths,
        string? clientId = null,
        string? tenant = null)
    {
        _tokens = new MicrosoftTokenStore(paths);
        _clientId = string.IsNullOrWhiteSpace(clientId)
            ? Environment.GetEnvironmentVariable("NPMC_MS_CLIENT_ID") ?? DefaultClientId
            : clientId;
        _tenant = string.IsNullOrWhiteSpace(tenant)
            ? Environment.GetEnvironmentVariable("NPMC_MS_TENANT") ?? "consumers"
            : tenant;
    }

    public string ClientId => _clientId;
    public string Tenant => _tenant;
    public MicrosoftTokenStore Tokens => _tokens;

    public async Task<MicrosoftDeviceCodeAuth.LoginResult> LoginAsync(
        Action<MicrosoftDeviceCodeAuth.DeviceCode>? onDeviceCode,
        CancellationToken cancellationToken = default)
    {
        using var http = CreateHttp();
        return await MicrosoftDeviceCodeAuth.LoginAsync(
            http, _clientId, _tenant, onDeviceCode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启动前取一份新的 Minecraft 令牌；refresh token 会轮换并重新加密保存。</summary>
    public async Task<MicrosoftDeviceCodeAuth.Result> GetMinecraftAsync(
        PlayerAccount account, CancellationToken cancellationToken = default)
    {
        var refresh = _tokens.Load(account.Id)
                      ?? throw new InvalidOperationException(
                          "没有保存的微软令牌，请重新登录这个账号");

        using var http = CreateHttp();

        var microsoft = await MicrosoftDeviceCodeAuth.RefreshAsync(
            http, _clientId, _tenant, refresh, cancellationToken).ConfigureAwait(false);

        // refresh token 可能轮换；先保存新的，再用 access token 换 Minecraft
        _tokens.Save(account.Id, microsoft.RefreshToken);

        return await MicrosoftDeviceCodeAuth.CompleteAsync(
            http, microsoft.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    public void SaveLogin(string accountId, MicrosoftDeviceCodeAuth.Tokens tokens)
        => _tokens.Save(accountId, tokens.RefreshToken);

    public void DeleteTokens(string accountId) => _tokens.Delete(accountId);

    private static HttpClient CreateHttp() => new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
}
