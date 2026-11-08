using System.Security.Cryptography;
using NPMC.P2P.Contracts;

namespace NPMC.P2P.DeviceIdentity.Tpm;

/// <summary>
/// TPM 业务密钥（ECDSA P-256 / SHA-256，私钥不可导出）。
///
/// - 持久化在 Microsoft Platform Crypto Provider 中，私钥永不离开 TPM；
/// - 只暴露公钥（SubjectPublicKeyInfo）用于 device_id / key_id 与服务器验证；
/// - 创建后会实际尝试导出私钥，必须失败。
/// </summary>
public sealed class TpmDeviceKey : IDisposable
{
    private const string AlgorithmName = "ECDSA_P256";

    private readonly ECDsaCng _ecdsa;
    private readonly byte[] _subjectPublicKeyInfo;
    private bool _deleted;

    private TpmDeviceKey(CngKey key, string keyName)
    {
        Key = key;
        KeyName = keyName;
        _ecdsa = new ECDsaCng(key);
        _subjectPublicKeyInfo = ExportSubjectPublicKeyInfo(_ecdsa, key);

        var digest = SHA256.HashData(_subjectPublicKeyInfo);
        DeviceId = "dev_" + Base64Url.Encode(digest);
        KeyId = "key_" + Base64Url.Encode(digest);
        PrivateKeyExportBlocked = CheckPrivateExportBlocked(key);
    }

    public CngKey Key { get; }

    public string KeyName { get; }

    public string DeviceId { get; }

    public string KeyId { get; }

    public byte[] SubjectPublicKeyInfo => (byte[])_subjectPublicKeyInfo.Clone();

    public bool UsesPlatformCryptoProvider =>
        Key.Provider is { } provider &&
        string.Equals(provider.Provider, PlatformCryptoProvider.ProviderName, StringComparison.Ordinal);

    public bool PrivateKeyExportBlocked { get; }

    public static TpmDeviceKey OpenOrCreate(string keyName)
    {
        return CngKey.Exists(keyName, new CngProvider(PlatformCryptoProvider.ProviderName))
            ? Open(keyName)
            : Create(keyName);
    }

    public static TpmDeviceKey Create(string keyName)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = new CngProvider(PlatformCryptoProvider.ProviderName),
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None,
            KeyUsage = CngKeyUsages.Signing,
        };

        var key = CngKey.Create(new CngAlgorithm(AlgorithmName), keyName, parameters);
        return new TpmDeviceKey(key, keyName);
    }

    public static TpmDeviceKey Open(string keyName)
    {
        var key = CngKey.Open(keyName, new CngProvider(PlatformCryptoProvider.ProviderName));
        return new TpmDeviceKey(key, keyName);
    }

    public static void DeletePersisted(string keyName)
    {
        if (!CngKey.Exists(keyName, new CngProvider(PlatformCryptoProvider.ProviderName)))
        {
            return;
        }

        using var key = CngKey.Open(keyName, new CngProvider(PlatformCryptoProvider.ProviderName));
        key.Delete();
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        ThrowIfDeleted();
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        ThrowIfDeleted();
        return _ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>删除 TPM 中持久化的密钥；之后对象不可再用。</summary>
    public void DeletePersistedKey()
    {
        if (_deleted)
        {
            return;
        }

        _ecdsa.Dispose();
        Key.Delete();
        _deleted = true;
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
        Key.Dispose();
    }

    private void ThrowIfDeleted()
    {
        if (_deleted)
        {
            throw new ObjectDisposedException(nameof(TpmDeviceKey), "TPM 密钥已被删除。");
        }
    }

    private static bool CheckPrivateExportBlocked(CngKey key)
    {
        try
        {
            var privateBlob = key.Export(CngKeyBlobFormat.EccPrivateBlob);
            CryptographicOperations.ZeroMemory(privateBlob);
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private static byte[] ExportSubjectPublicKeyInfo(ECDsaCng ecdsa, CngKey key)
    {
        try
        {
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            return BuildP256SpkiFromPublicBlob(key);
        }
        catch (NotSupportedException)
        {
            return BuildP256SpkiFromPublicBlob(key);
        }
    }

    /// <summary>
    /// 手工构造 P-256 SubjectPublicKeyInfo（91 字节）。
    /// BCRYPT_ECCKEY_BLOB = dwMagic(4) + cbKey(4) + X(32) + Y(32)。
    /// </summary>
    private static byte[] BuildP256SpkiFromPublicBlob(CngKey key)
    {
        var blob = key.Export(CngKeyBlobFormat.EccPublicBlob);
        if (blob.Length != 72)
        {
            throw new CryptographicException($"非预期的 ECC 公钥长度：{blob.Length}");
        }

        var cbKey = BitConverter.ToInt32(blob, 4);
        if (cbKey != 32)
        {
            throw new CryptographicException($"非 P-256 曲线：cbKey={cbKey}");
        }

        ReadOnlySpan<byte> prefix = new byte[]
        {
            0x30, 0x59,
            0x30, 0x13,
            0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07,
            0x03, 0x42, 0x00, 0x04,
        };

        var spki = new byte[prefix.Length + 64];
        prefix.CopyTo(spki);
        blob.AsSpan(8, 32).CopyTo(spki.AsSpan(prefix.Length));
        blob.AsSpan(40, 32).CopyTo(spki.AsSpan(prefix.Length + 32));
        return spki;
    }
}
