using System.Text.Json.Serialization;

namespace NPMC.P2P.Contracts.NamedPipe;

/// <summary>Named Pipe 帧与请求类型常量。</summary>
public static class NamedPipeProtocol
{
    public const int Version = 1;

    /// <summary>4 字节小端长度前缀。</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>单帧上限（含长度前缀之外的 JSON 主体）。</summary>
    public const int MaxMessageBytes = 8192;

    public const string Hello = "hello";
    public const string SignChallenge = "sign_challenge";

    public static readonly string[] RequestTypes = { Hello, SignChallenge };
}

public static class PipeErrorCodes
{
    public const string InvalidFrame = "invalid_frame";
    public const string InvalidRequest = "invalid_request";
    public const string UnknownType = "unknown_type";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string BadSession = "bad_session";
    public const string BadClient = "bad_client";
    public const string Replay = "replay";
    public const string StaleTimestamp = "stale_timestamp";
    public const string RateLimited = "rate_limited";
    public const string InvalidBinding = "invalid_binding";
    public const string InvalidChallenge = "invalid_challenge";
    public const string TpmUnavailable = "tpm_unavailable";
    public const string SignFailed = "sign_failed";
    public const string InternalError = "internal_error";
}

public sealed class SignChallengeRequest
{
    [JsonPropertyName("challenge")]
    public string Challenge { get; set; } = string.Empty;

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = string.Empty;

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("profile_id")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("game_session_id")]
    public string GameSessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gateway 在 Login Plugin Request 里下发的 nonce；p2p:auth 签名会把它作为
    /// AuthChallengePayload.server_nonce。为空时沿用管道自身的 server_nonce。
    /// </summary>
    [JsonPropertyName("gateway_nonce")]
    public string? GatewayNonce { get; set; }
}

public sealed class PipeRequest
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("session_token")]
    public string SessionToken { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("client_nonce")]
    public string ClientNonce { get; set; } = string.Empty;

    [JsonPropertyName("server_nonce")]
    public string? ServerNonce { get; set; }

    [JsonPropertyName("timestamp_ms")]
    public long TimestampUnixMs { get; set; }

    [JsonPropertyName("payload")]
    public SignChallengeRequest? Payload { get; set; }
}

public sealed class SignChallengeResult
{
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("signed_payload")]
    public string SignedPayload { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("signed_at_ms")]
    public long SignedAtUnixMs { get; set; }

    /// <summary>业务公钥（SubjectPublicKeyInfo，base64url），供 Gateway 验证签名。</summary>
    [JsonPropertyName("public_key")]
    public string? PublicKey { get; set; }
}

public sealed class PipeResponse
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = NamedPipeProtocol.Version;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("server_nonce")]
    public string? ServerNonce { get; set; }

    [JsonPropertyName("result")]
    public SignChallengeResult? Result { get; set; }
}
