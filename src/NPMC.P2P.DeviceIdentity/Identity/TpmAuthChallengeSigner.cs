using NPMC.P2P.Contracts;
using NPMC.P2P.Contracts.NamedPipe;
using NPMC.P2P.Contracts.Signing;

namespace NPMC.P2P.DeviceIdentity.Identity;

/// <summary>签名器接口：Named Pipe 服务端只依赖此接口，便于测试替换。</summary>
public interface IAuthChallengeSigner
{
    string DeviceId { get; }

    string KeyId { get; }

    SignChallengeResult SignAuthChallenge(AuthChallengePayload payload);
}

/// <summary>用 TPM 业务密钥签署 Gateway 挑战声明。</summary>
public sealed class TpmAuthChallengeSigner : IAuthChallengeSigner
{
    private readonly Tpm.TpmDeviceKey _key;

    public TpmAuthChallengeSigner(Tpm.TpmDeviceKey key)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public string DeviceId => _key.DeviceId;

    public string KeyId => _key.KeyId;

    public SignChallengeResult SignAuthChallenge(AuthChallengePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.Validate();

        var bytes = payload.ToUtf8Bytes();
        var signature = _key.Sign(bytes);

        return new SignChallengeResult
        {
            Signature = Base64Url.Encode(signature),
            SignedPayload = Base64Url.Encode(bytes),
            DeviceId = _key.DeviceId,
            KeyId = _key.KeyId,
            SignedAtUnixMs = payload.TimestampUnixMs,
            PublicKey = Base64Url.Encode(_key.SubjectPublicKeyInfo),
        };
    }
}
