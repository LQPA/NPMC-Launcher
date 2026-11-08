using System.Runtime.InteropServices;

namespace NPMC.P2P.DeviceIdentity.Windows;

/// <summary>TPM 设备信息（通过 TBS 读取，普通用户权限即可）。</summary>
public readonly record struct TpmDeviceInfo(uint TpmVersion, uint InterfaceType, uint ImpRevision)
{
    public bool IsTpm20 => TpmVersion == 2;

    public string VersionText => TpmVersion switch
    {
        1 => "TPM 1.2",
        2 => "TPM 2.0",
        _ => $"未知（{TpmVersion}）",
    };
}

/// <summary>
/// TBS（tbs.dll）是 Windows 提供的 TPM 访问入口，不需要管理员权限即可查询版本。
/// 查询失败（无 TPM、服务未运行、被策略禁用）一律视为“不可用”，绝不猜测。
/// </summary>
public static class Tbs
{
    public const uint ErrorSuccess = 0;

    public static TpmDeviceInfo? TryQuery() => TryQuery(out var info, out _) ? info : null;

    public static bool TryQuery(out TpmDeviceInfo info, out uint status)
    {
        info = default;
        status = 0;

        var native = new NativeMethods.TPM_DEVICE_INFO { StructVersion = 1 };
        try
        {
            status = NativeMethods.Tbsi_GetDeviceInfo(
                (uint)Marshal.SizeOf<NativeMethods.TPM_DEVICE_INFO>(),
                ref native);
        }
        catch (DllNotFoundException)
        {
            status = 0x8007007E; // ERROR_MOD_NOT_FOUND
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            status = 0x8007007F; // ERROR_PROC_NOT_FOUND
            return false;
        }

        if (status != ErrorSuccess)
        {
            return false;
        }

        info = new TpmDeviceInfo(native.TpmVersion, native.TpmInterfaceType, native.TpmImpRevision);
        return true;
    }
}
