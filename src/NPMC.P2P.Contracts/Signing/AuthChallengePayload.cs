using NPMC.P2P.Contracts.Validation;

namespace NPMC.P2P.Contracts.Signing;

/// <summary>
/// Gateway 登录阶段（或本地模拟）发给设备的挑战声明。
/// 设备对 <see cref="ToStatement"/> 的规范化字节签名，证明“当前设备的业务密钥响应了这个挑战，
/// 且该签名只对当前 room/node/profile/game-session 有效”。
/// </summary>
public sealed record AuthChallengePayload(
    string Challenge,
    string ClientNonce,
    string DeviceId,
    string GameSessionId,
    string NodeId,
    string ProfileId,
    string RoomId,
    string ServerNonce,
    long TimestampUnixMs)
{
    public const string StatementType = P2PProtocol.AuthChallengeType;

    private static readonly string[] RequiredFields =
    {
        "challenge",
        "client_nonce",
        "device_id",
        "game_session_id",
        "node_id",
        "profile_id",
        "room_id",
        "server_nonce",
        "timestamp_ms",
    };

    public SigningStatement ToStatement()
    {
        Validate();
        return SigningStatement.Create(StatementType, new[]
        {
            Field("challenge", Challenge),
            Field("client_nonce", ClientNonce),
            Field("device_id", DeviceId),
            Field("game_session_id", GameSessionId),
            Field("node_id", NodeId),
            Field("profile_id", ProfileId),
            Field("room_id", RoomId),
            Field("server_nonce", ServerNonce),
            Field("timestamp_ms", TimestampUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        });
    }

    public byte[] ToUtf8Bytes() => ToStatement().ToUtf8Bytes();

    public static AuthChallengePayload FromStatement(SigningStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        if (!string.Equals(statement.Type, StatementType, StringComparison.Ordinal))
        {
            throw new FormatException($"声明类型不是 {StatementType}：{statement.Type}");
        }

        if (statement.Fields.Count != RequiredFields.Length)
        {
            throw new FormatException("auth-challenge 字段数量不符。");
        }

        foreach (var field in RequiredFields)
        {
            if (!statement.TryGetField(field, out _))
            {
                throw new FormatException($"auth-challenge 缺少字段 {field}。");
            }
        }

        if (!long.TryParse(
                statement.GetField("timestamp_ms"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var timestamp))
        {
            throw new FormatException("timestamp_ms 不是合法整数。");
        }

        var payload = new AuthChallengePayload(
            statement.GetField("challenge"),
            statement.GetField("client_nonce"),
            statement.GetField("device_id"),
            statement.GetField("game_session_id"),
            statement.GetField("node_id"),
            statement.GetField("profile_id"),
            statement.GetField("room_id"),
            statement.GetField("server_nonce"),
            timestamp);

        payload.Validate();
        return payload;
    }

    public void Validate()
    {
        ChallengeRules.ValidateChallenge(Challenge, nameof(Challenge));
        ChallengeRules.ValidateNonce(ClientNonce, nameof(ClientNonce));
        ChallengeRules.ValidateNonce(ServerNonce, nameof(ServerNonce));
        IdentifierRules.Validate(DeviceId, nameof(DeviceId));
        IdentifierRules.Validate(GameSessionId, nameof(GameSessionId));
        IdentifierRules.Validate(NodeId, nameof(NodeId));
        IdentifierRules.Validate(ProfileId, nameof(ProfileId));
        IdentifierRules.Validate(RoomId, nameof(RoomId));

        if (TimestampUnixMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TimestampUnixMs), "时间戳必须为正。");
        }
    }

    private static KeyValuePair<string, string> Field(string key, string value) => new(key, value);
}
