using System.Security.Cryptography;
using NPMC.P2P.Contracts;
using NPMC.P2P.Contracts.Validation;

namespace NPMC.P2P.DeviceIdentity.Identity;

/// <summary>
/// 一次游戏会话的绑定：房间 / 节点 / 档案 / 游戏会话 + 一次性令牌。
/// 启动器通过 <c>-javaagent:...=token=...</c> 把 <see cref="SessionToken"/> 交给 Agent；
/// Named Pipe 只接受同时持有令牌且绑定字段完全一致的请求。
/// </summary>
public sealed class GameSessionBinding
{
    private readonly byte[] _tokenHash;

    private GameSessionBinding(
        string roomId,
        string nodeId,
        string profileId,
        string gameSessionId,
        string sessionToken)
    {
        RoomId = roomId;
        NodeId = nodeId;
        ProfileId = profileId;
        GameSessionId = gameSessionId;
        SessionToken = sessionToken;
        _tokenHash = SHA256.HashData(Base64Url.Decode(sessionToken));
    }

    public string RoomId { get; }

    public string NodeId { get; }

    public string ProfileId { get; }

    public string GameSessionId { get; }

    /// <summary>32 字节随机令牌的 base64url 表示；只交给本次启动的 Agent 进程。</summary>
    public string SessionToken { get; }

    public static GameSessionBinding Create(
        string roomId,
        string nodeId,
        string profileId,
        string gameSessionId)
    {
        IdentifierRules.Validate(roomId, nameof(roomId));
        IdentifierRules.Validate(nodeId, nameof(nodeId));
        IdentifierRules.Validate(profileId, nameof(profileId));
        IdentifierRules.Validate(gameSessionId, nameof(gameSessionId));

        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        return new GameSessionBinding(roomId, nodeId, profileId, gameSessionId, token);
    }

    /// <summary>使用调用方提供的令牌（仅测试/恢复场景）。</summary>
    public static GameSessionBinding CreateWithToken(
        string roomId,
        string nodeId,
        string profileId,
        string gameSessionId,
        string sessionToken)
    {
        IdentifierRules.Validate(roomId, nameof(roomId));
        IdentifierRules.Validate(nodeId, nameof(nodeId));
        IdentifierRules.Validate(profileId, nameof(profileId));
        IdentifierRules.Validate(gameSessionId, nameof(gameSessionId));

        if (!Base64Url.TryDecode(sessionToken, out var bytes) || bytes.Length != 32)
        {
            throw new ArgumentException("session_token 必须是 32 字节 base64url。", nameof(sessionToken));
        }

        return new GameSessionBinding(roomId, nodeId, profileId, gameSessionId, sessionToken);
    }

    public bool Matches(string roomId, string nodeId, string profileId, string gameSessionId)
    {
        return string.Equals(RoomId, roomId, StringComparison.Ordinal)
               && string.Equals(NodeId, nodeId, StringComparison.Ordinal)
               && string.Equals(ProfileId, profileId, StringComparison.Ordinal)
               && string.Equals(GameSessionId, gameSessionId, StringComparison.Ordinal);
    }

    /// <summary>常量时间比较令牌哈希，避免通过响应时间猜令牌。</summary>
    public bool IsValidToken(string token)
    {
        if (!Base64Url.TryDecode(token, out var bytes) || bytes.Length != 32)
        {
            return false;
        }

        var presented = SHA256.HashData(bytes);
        return CryptographicOperations.FixedTimeEquals(presented, _tokenHash);
    }
}
