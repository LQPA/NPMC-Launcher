using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NPMC.P2P.DeviceIdentity.Windows;

/// <summary>Named Pipe 客户端进程的可核验信息。</summary>
public sealed record ClientProcessInfo(
    int ProcessId,
    string ImageName,
    string UserSid,
    int SessionId,
    int ParentProcessId);

public enum ClientProcessLookupFailure
{
    None,
    ProcessIdUnavailable,
    OpenProcessFailed,
    ImageNameUnavailable,
    TokenUnavailable,
    Unknown,
}

/// <summary>
/// 只读检查客户端进程：映像名、用户 SID、会话 ID、父进程 PID。
/// 任一读取失败都返回失败（fail closed），绝不把“未知”当成“通过”。
/// </summary>
public static class ClientProcessInspector
{
    public static bool TryDescribe(
        int processId,
        out ClientProcessInfo info,
        out ClientProcessLookupFailure failure)
    {
        info = null!;
        failure = ClientProcessLookupFailure.None;

        if (processId <= 0)
        {
            failure = ClientProcessLookupFailure.ProcessIdUnavailable;
            return false;
        }

        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            (uint)processId);

        if (handle == IntPtr.Zero)
        {
            failure = ClientProcessLookupFailure.OpenProcessFailed;
            return false;
        }

        try
        {
            var imageName = TryGetImageName(handle);
            if (imageName is null)
            {
                failure = ClientProcessLookupFailure.ImageNameUnavailable;
                return false;
            }

            var userSid = TryGetUserSid(handle);
            if (userSid is null)
            {
                failure = ClientProcessLookupFailure.TokenUnavailable;
                return false;
            }

            if (!NativeMethods.ProcessIdToSessionId((uint)processId, out var sessionId))
            {
                failure = ClientProcessLookupFailure.Unknown;
                return false;
            }

            var parentPid = TryGetParentProcessId(handle);
            if (parentPid is null)
            {
                failure = ClientProcessLookupFailure.Unknown;
                return false;
            }

            info = new ClientProcessInfo(processId, imageName, userSid, (int)sessionId, parentPid.Value);
            return true;
        }
        catch (Exception)
        {
            failure = ClientProcessLookupFailure.Unknown;
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public static bool TryGetClientProcessId(SafePipeHandle pipe, out int processId)
    {
        if (NativeMethods.GetNamedPipeClientProcessId(pipe, out var pid))
        {
            processId = (int)pid;
            return true;
        }

        processId = 0;
        return false;
    }

    private static string? TryGetImageName(IntPtr processHandle)
    {
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        if (!NativeMethods.QueryFullProcessImageNameW(processHandle, 0, buffer, ref size))
        {
            return null;
        }

        var path = buffer.ToString();
        return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
    }

    private static string? TryGetUserSid(IntPtr processHandle)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserInformationClass, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return null;
            }

            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token,
                        NativeMethods.TokenUserInformationClass,
                        buffer,
                        length,
                        out _))
                {
                    return null;
                }

                var tokenUser = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.TOKEN_USER>(buffer);
                if (tokenUser.User.Sid == IntPtr.Zero)
                {
                    return null;
                }

                return new SecurityIdentifier(tokenUser.User.Sid).Value;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static int? TryGetParentProcessId(IntPtr processHandle)
    {
        var info = default(NativeMethods.PROCESS_BASIC_INFORMATION);
        var status = NativeMethods.NtQueryInformationProcess(
            processHandle,
            NativeMethods.ProcessBasicInformationClass,
            ref info,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(),
            out _);

        if (status != 0)
        {
            return null;
        }

        return info.InheritedFromUniqueProcessId.ToInt32();
    }
}
