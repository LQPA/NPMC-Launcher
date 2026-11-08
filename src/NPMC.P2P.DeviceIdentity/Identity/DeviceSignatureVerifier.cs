using System.Security.Cryptography;

namespace NPMC.P2P.DeviceIdentity.Identity;

/// <summary>
/// 设备签名验证（服务器 / Gateway / 本地 mock 共用）。
/// 只接受 SubjectPublicKeyInfo；任何解析或验证异常都按“验证失败”处理（fail closed）。
/// </summary>
public static class DeviceSignatureVerifier
{
    public static bool Verify(byte[] subjectPublicKeyInfo, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (subjectPublicKeyInfo is null || subjectPublicKeyInfo.Length == 0)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
