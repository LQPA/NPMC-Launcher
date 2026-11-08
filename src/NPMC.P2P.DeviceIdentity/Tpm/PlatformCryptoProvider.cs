using NPMC.P2P.DeviceIdentity.Windows;

namespace NPMC.P2P.DeviceIdentity.Tpm;

/// <summary>
/// Microsoft Platform Crypto Provider（CNG KSP）可用性检查。
/// 只打开/关闭 Provider 句柄，不创建密钥。
/// </summary>
public static class PlatformCryptoProvider
{
    public const string ProviderName = "Microsoft Platform Crypto Provider";

    public static bool TryOpen(out int nCryptStatus)
    {
        nCryptStatus = NativeMethods.NCryptOpenStorageProvider(out var handle, ProviderName, 0);
        if (nCryptStatus != 0)
        {
            return false;
        }

        NativeMethods.NCryptFreeObject(handle);
        return true;
    }
}
