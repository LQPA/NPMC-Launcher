using System.Security.Cryptography;
using System.Text.Json.Serialization;
using NPMC.P2P.Contracts.Signing;
using NPMC.P2P.Contracts.Validation;

namespace NPMC.P2P.Contracts.Auth;

/// <summary>Login 阶段 P2P 设备认证的常量与 DTO。</summary>
public static class P2PAuthProtocol
{
    public const int Version = 1;

    /// <summary>Login Plugin Request 的 channel 名。</summary>
    public const string Channel = "p2p:auth";

    /// <summary>插件数据上限（JSON，字节）。</summary>
    public const int MaxPayloadBytes = 64 * 1024;
}

/// <summary>Gateway 发给 Agent 的挑战（Login Plugin Request data）。</summary>
public sealed class P2PAuthChallenge
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = P2PAuthProtocol.Version;

    [JsonPropertyName("challenge")]
    public string Challenge { get; set; } = string.Empty;

    [JsonPropertyName("gateway_nonce")]
    public string GatewayNonce { get; set; } = string.Empty;

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = string.Empty;

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("profile_id")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("game_session_id")]
    public string GameSessionId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp_ms")]
    public long TimestampUnixMs { get; set; }

    public void Validate()
    {
        if (ProtocolVersion != P2PAuthProtocol.Version)
        {
            throw new ArgumentException($"不支持的 p2p:auth 协议版本：{ProtocolVersion}");
        }

        ChallengeRules.ValidateChallenge(Challenge, nameof(Challenge));
        ChallengeRules.ValidateNonce(GatewayNonce, nameof(GatewayNonce));
        IdentifierRules.Validate(RoomId, nameof(RoomId));
        IdentifierRules.Validate(NodeId, nameof(NodeId));
        IdentifierRules.Validate(ProfileId, nameof(ProfileId));
        IdentifierRules.Validate(GameSessionId, nameof(GameSessionId));

        if (TimestampUnixMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TimestampUnixMs));
        }
    }
}

/// <summary>Agent 返回给 Gateway 的证明（Login Plugin Response data）。</summary>
public sealed class P2PAuthProof
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = P2PAuthProtocol.Version;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("signed_payload")]
    public string SignedPayload { get; set; } = string.Empty;

    public void Validate()
    {
        if (ProtocolVersion != P2PAuthProtocol.Version)
        {
            throw new ArgumentException($"不支持的 p2p:auth 协议版本：{ProtocolVersion}");
        }

        IdentifierRules.Validate(DeviceId, nameof(DeviceId));
        IdentifierRules.Validate(KeyId, nameof(KeyId));

        if (!Base64Url.TryDecode(PublicKey, out var spki) || spki.Length is < 64 or > 256)
        {
            throw new ArgumentException("public_key 不是合法的 SubjectPublicKeyInfo。", nameof(PublicKey));
        }

        if (!Base64Url.TryDecode(Signature, out var signature) || signature.Length is < 64 or > 128)
        {
            throw new ArgumentException("signature 长度不合法。", nameof(Signature));
        }

        if (!Base64Url.TryDecode(SignedPayload, out var payload) || payload.Length == 0 ||
            payload.Length > P2PAuthProtocol.MaxPayloadBytes)
        {
            throw new ArgumentException("signed_payload 长度不合法。", nameof(SignedPayload));
        }
    }
}

/// <summary>设备 ID 派生规则（原型：由公钥哈希得到；服务器权威 ID 在 D4）。</summary>
public static class DeviceIdRules
{
    public static string DeriveDeviceId(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        return "dev_" + Base64Url.Encode(SHA256.HashData(subjectPublicKeyInfo));
    }

    public static string DeriveKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        return "key_" + Base64Url.Encode(SHA256.HashData(subjectPublicKeyInfo));
    }
}
