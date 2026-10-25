using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NPMC.Launcher.App.Interop;

/// <summary>
/// 当前进程的执行上下文信息，用于排查"启动外部程序被拒"这类问题。
///
/// 完整性级别**直接读自己的进程令牌**（GetTokenInformation + TokenIntegrityLevel），
/// 而不是解析 whoami.exe 的输出 —— 后者依赖子进程、依赖文本格式、还可能被重定向，
/// 排查这种问题时不能再引入新的不确定性。
/// whoami 的结果只保留在诊断里做交叉核对。
/// </summary>
internal static class ProcessContext
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private static readonly Lazy<int?> CachedRid = new(ReadIntegrityRidCore);

    /// <summary>完整性级别的 RID（4096 = Low，8192 = Medium，12288 = High）。读取失败为 null。</summary>
    public static int? IntegrityRid => CachedRid.Value;

    public static string IntegrityName => IntegrityRid switch
    {
        0x0000 => "Untrusted",
        0x1000 => "Low",
        0x2000 => "Medium",
        0x3000 => "High",
        0x4000 => "System",
        null => "未知",
        var rid => $"0x{rid:X4}",
    };

    /// <summary>
    /// Low 完整性会同时导致：跨进程 shell COM 被拒（0x80070005）、
    /// ShellExecute 被拒（"拒绝访问"）、explorer.exe 启动失败（0xC0000142）。
    /// 也就是说这种进程里"打开文件夹"无论如何都做不到。
    /// </summary>
    public static bool IsLowIntegrity => IntegrityRid is { } rid && rid < 0x2000;

    /// <summary>形如 "Medium (0x2000)"，用于界面与日志。</summary>
    public static string Describe() =>
        IntegrityRid is { } rid ? $"{IntegrityName} (0x{rid:X4})" : "未知";

    /// <summary>交叉核对用：直接返回 whoami 里那行强制标签原文。</summary>
    public static string ReadWhoAmIOutOfCuriosity()
    {
        try
        {
            var startInfo = new ProcessStartInfo("whoami.exe", "/groups")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);
            if (process is null) return "(无法执行 whoami)";

            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("S-1-16-", StringComparison.OrdinalIgnoreCase))
                    return line.Trim();
            }

            return "(未找到强制标签行)";
        }
        catch (Exception ex)
        {
            return $"(读取失败: {ex.GetBaseException().Message})";
        }
    }

    private static int? ReadIntegrityRidCore()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return null;

            // 先问长度，再取数据
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;

            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return null;

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; }，首字段就是 SID 指针
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return null;

            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero) return null;

            var count = Marshal.ReadByte(countPointer);
            if (count == 0) return null;

            // 最后一个子授权就是完整性级别本身
            var subAuthority = GetSidSubAuthority(sid, (uint)(count - 1));
            if (subAuthority == IntPtr.Zero) return null;

            return Marshal.ReadInt32(subAuthority);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
