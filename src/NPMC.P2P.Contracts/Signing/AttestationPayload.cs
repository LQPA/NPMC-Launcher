using NPMC.P2P.Contracts.Validation;

namespace NPMC.P2P.Contracts.Signing;

/// <summary>
/// 设备证明声明（Gate 0-B 用模拟服务器验证；D3 换成真实 EK/AK 证明）。
/// 服务器生成随机 nonce，设备用业务密钥签名；服务器用设备公钥验证。
/// </summary>
public sealed record AttestationPayload(
    string Challenge,
    string DeviceId,
    string KeyId,
    long TimestampUnixMs)
{
    public const string StatementType = P2PProtocol.AttestationType;

    private static readonly string[] RequiredFields =
    {
        "challenge",
        "device_id",
        "key_id",
        "timestamp_ms",
    };

    public SigningStatement ToStatement()
    {
        Validate();
        return SigningStatement.Create(StatementType, new[]
        {
            Field("challenge", Challenge),
            Field("device_id", DeviceId),
            Field("key_id", KeyId),
            Field("timestamp_ms", TimestampUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        });
    }

    public byte[] ToUtf8Bytes() => ToStatement().ToUtf8Bytes();

    public static AttestationPayload FromStatement(SigningStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        if (!string.Equals(statement.Type, StatementType, StringComparison.Ordinal))
        {
            throw new FormatException($"声明类型不是 {StatementType}：{statement.Type}");
        }

        if (statement.Fields.Count != RequiredFields.Length)
        {
            throw new FormatException("attest 字段数量不符。");
        }

        foreach (var field in RequiredFields)
        {
            if (!statement.TryGetField(field, out _))
            {
                throw new FormatException($"attest 缺少字段 {field}。");
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

        var payload = new AttestationPayload(
            statement.GetField("challenge"),
            statement.GetField("device_id"),
            statement.GetField("key_id"),
            timestamp);

        payload.Validate();
        return payload;
    }

    public void Validate()
    {
        ChallengeRules.ValidateChallenge(Challenge, nameof(Challenge));
        IdentifierRules.Validate(DeviceId, nameof(DeviceId));
        IdentifierRules.Validate(KeyId, nameof(KeyId));

        if (TimestampUnixMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TimestampUnixMs), "时间戳必须为正。");
        }
    }

    private static KeyValuePair<string, string> Field(string key, string value) => new(key, value);
}
