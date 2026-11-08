using System.Security.Cryptography;
using NPMC.P2P.Contracts;
using NPMC.P2P.Contracts.Signing;
using NPMC.P2P.DeviceIdentity.Identity;
using NPMC.P2P.DeviceIdentity.Tpm;

namespace NPMC.P2P.DeviceIdentity.Attestation;

/// <summary>
/// Gate 0-B 的“模拟远程证明服务器”：
/// 服务器发 nonce → 设备用 TPM 业务密钥签名 → 服务器用设备公钥验证。
///
/// 这不是生产证明：真实 EK/AK 证书链与密钥属性验证在 D3 实现。
/// 单次使用 + 有效期，防止重放。
/// </summary>
public sealed class MockAttestationServer
{
    private readonly Dictionary<string, DateTimeOffset> _issuedChallenges = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _challengeLifetime;

    public MockAttestationServer(TimeSpan? challengeLifetime = null)
    {
        _challengeLifetime = challengeLifetime ?? TimeSpan.FromMinutes(2);
    }

    public string IssueChallenge()
    {
        var challenge = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _issuedChallenges[challenge] = DateTimeOffset.UtcNow;
        }

        return challenge;
    }

    public bool VerifyAndConsume(
        AttestationPayload payload,
        byte[] signature,
        byte[] subjectPublicKeyInfo,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);

        lock (_gate)
        {
            if (!_issuedChallenges.TryGetValue(payload.Challenge, out var issuedAt))
            {
                return false;
            }

            // 无论成功失败，挑战都只能尝试一次；失败也必须移除，避免被反复试探。
            _issuedChallenges.Remove(payload.Challenge);

            if (now - issuedAt > _challengeLifetime || now < issuedAt - TimeSpan.FromMinutes(1))
            {
                return false;
            }

            return DeviceSignatureVerifier.Verify(
                subjectPublicKeyInfo,
                payload.ToUtf8Bytes(),
                signature);
        }
    }
}

/// <summary>设备侧：用 TPM 业务密钥响应模拟证明挑战。</summary>
public sealed class TpmAttestationSigner
{
    private readonly TpmDeviceKey _key;

    public TpmAttestationSigner(TpmDeviceKey key)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public AttestationPayload CreatePayload(string challenge, DateTimeOffset now)
    {
        var payload = new AttestationPayload(
            challenge,
            _key.DeviceId,
            _key.KeyId,
            now.ToUnixTimeMilliseconds());
        payload.Validate();
        return payload;
    }

    public byte[] Sign(AttestationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _key.Sign(payload.ToUtf8Bytes());
    }

    public (AttestationPayload Payload, byte[] Signature) CreateAndSign(string challenge, DateTimeOffset now)
    {
        var payload = CreatePayload(challenge, now);
        return (payload, Sign(payload));
    }
}
