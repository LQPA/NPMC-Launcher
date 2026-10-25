using System.Runtime.InteropServices;

namespace NPMC.Launcher.Infrastructure.Launch;

/// <summary>
/// Windows 强制完整性级别（mandatory integrity level）。
/// 枚举值就是完整性 SID（S-1-16-x）最后一个子授权的数值，便于直接比较。
/// </summary>
public enum WindowsIntegrityLevel
{
    /// <summary>读取失败，无法判断。</summary>
    Unknown = -1,

    /// <summary>没有显式标签；启动时按父进程/令牌级别运行。</summary>
    None = -2,

    Untrusted = 0x0000,
    Low = 0x1000,
    Medium = 0x2000,
    High = 0x3000,
    System = 0x4000,
}

/// <summary>
/// 一次 Java 可执行文件完整性检查的结果。
/// <see cref="RaisedTo"/> 非空表示本次真的改了标签；<see cref="Problem"/> 非空表示没修好。
/// </summary>
public readonly record struct IntegrityCheckResult(
    WindowsIntegrityLevel ProcessLevel,
    WindowsIntegrityLevel ExecutableLevel,
    WindowsIntegrityLevel? RaisedTo,
    string? Problem)
{
    public bool Repaired => RaisedTo is not null;
}

/// <summary>
/// 保证 Java 可执行文件**不会以比启动器更低的完整性级别运行**。
///
/// 为什么需要：
/// 1. Windows 创建进程时会取“令牌级别”和“镜像文件标签”中较低者作为进程完整性级别。
///    所以只要 java.exe 文件带 Low 标签（例如整个工作区被标成 Low，子文件全部继承），
///    即使启动器本身是 Medium，JVM 也会以 Low 运行。
/// 2. JDK 的 Files.isWritable 在 Windows 上走
///    WindowsAclFileAttributeView.getFileSecurity(path, 7) + AccessCheck，
///    取到的安全描述符**只有 OWNER/GROUP/DACL，没有完整性标签**。
///    于是 AccessCheck 把对象当成默认（Medium）级别：Low 进程请求写权限时命中
///    “禁止向上写”，**对所有文件都返回 false**——包括它刚创建、刚刚写成功的文件。
/// 3. Fabric 的 zipfs 用 Files.isWritable 决定只读标志（JDK 21 ZipFileSystem 第 173 行），
///    tinyremapper 再据此拒绝写入 client-intermediary.jar.tmp，重映射必然失败。
///
/// 因此这里在启动前把 Java 可执行文件的标签提升到 Medium（仅当它确实更低时），
/// 让 JVM 与启动器同级。正常情况下（没有标签）这段逻辑什么也不做。
/// </summary>
public static class IntegrityGuard
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint LabelSecurityInformation = 0x0010;
    private const int AclRevision = 2;
    private const byte MandatoryLabelAceType = 0x11;
    private const uint SystemMandatoryLabelNoWriteUp = 0x00000001;

    /// <summary>Java 低于这个级别时，Fabric/Quilt 的重映射一定会失败。</summary>
    public const WindowsIntegrityLevel MinimumUsableLevel = WindowsIntegrityLevel.Medium;

    /// <summary>
    /// 启动 Java 前的最后一道检查：必要时把 java.exe 的完整性标签提到 Medium。
    /// 只读检查失败（非 Windows、文件不存在、查询失败）时不做任何修改，也不报错。
    /// </summary>
    public static IntegrityCheckResult EnsureExecutableRunsAtLauncherLevel(string executablePath)
    {
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            return new IntegrityCheckResult(
                WindowsIntegrityLevel.Unknown, WindowsIntegrityLevel.Unknown, null, null);
        }

        var process = TryGetProcessLevel();
        var executable = TryGetFileLevel(executablePath);

        if (process is null)
        {
            return new IntegrityCheckResult(
                WindowsIntegrityLevel.Unknown,
                executable ?? WindowsIntegrityLevel.Unknown,
                null, null);
        }

        // Java 的完整性级别 = min(令牌, 镜像标签)。启动器自己就是 Low 的话，
        // 无论文件标签如何，JVM 都一定 ≤ Low —— 这时只能报错，改不了。
        if (process.Value < MinimumUsableLevel)
        {
            return new IntegrityCheckResult(
                process.Value,
                executable ?? WindowsIntegrityLevel.Unknown,
                null,
                DescribeLowIntegrity(
                    executablePath, executable ?? WindowsIntegrityLevel.Unknown, process.Value));
        }

        // 标签读不到（而不是“没有标签”）就不动文件，避免误判。
        if (executable is null)
            return new IntegrityCheckResult(process.Value, WindowsIntegrityLevel.Unknown, null, null);

        var effective = EffectiveLevel(executable.Value, process.Value);

        // 标签缺失时按令牌级别运行；令牌本身是 Medium 以上就不会有问题。
        if (effective is WindowsIntegrityLevel.Unknown || effective >= MinimumUsableLevel)
        {
            return new IntegrityCheckResult(process.Value, executable.Value, null, null);
        }

        if (!TryRaiseFileLevel(executablePath, MinimumUsableLevel, out var error))
        {
            return new IntegrityCheckResult(
                process.Value, executable.Value, null,
                DescribeRaiseFailure(executablePath, executable.Value, error));
        }

        var after = TryGetFileLevel(executablePath) ?? executable.Value;
        if (after < MinimumUsableLevel)
        {
            return new IntegrityCheckResult(
                process.Value, executable.Value, null,
                DescribeRaiseFailure(executablePath, executable.Value, "写入后标签仍未生效"));
        }

        return new IntegrityCheckResult(process.Value, executable.Value, after, null);
    }

    /// <summary>进程实际会得到的完整性级别：取令牌与镜像标签中较低者。</summary>
    public static WindowsIntegrityLevel EffectiveLevel(
        WindowsIntegrityLevel executable,
        WindowsIntegrityLevel process)
    {
        if (process is WindowsIntegrityLevel.Unknown ||
            executable is WindowsIntegrityLevel.Unknown)
        {
            return WindowsIntegrityLevel.Unknown;
        }

        // 没有标签的镜像沿用令牌级别。
        if (executable is WindowsIntegrityLevel.None) return process;

        return executable < process ? executable : process;
    }

    /// <summary>是否需要把标签提上去（只有“标签确实更低”才需要改文件）。</summary>
    public static bool NeedsRaise(WindowsIntegrityLevel executable, WindowsIntegrityLevel process)
        => EffectiveLevel(executable, process) < MinimumUsableLevel
           && process >= MinimumUsableLevel
           && executable is not (WindowsIntegrityLevel.Unknown or WindowsIntegrityLevel.None);

    /// <summary>把 "S-1-16-4096" 这样的字符串解析成级别；解析不了返回 Unknown。</summary>
    public static WindowsIntegrityLevel LevelFromSidString(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return WindowsIntegrityLevel.Unknown;

        var parts = sid.Split('-');
        if (parts.Length < 4 || parts[0] != "S" || parts[1] != "1" || parts[2] != "16")
            return WindowsIntegrityLevel.Unknown;

        if (!uint.TryParse(parts[^1], out var rid)) return WindowsIntegrityLevel.Unknown;

        return rid switch
        {
            0x0000 => WindowsIntegrityLevel.Untrusted,
            0x1000 => WindowsIntegrityLevel.Low,
            0x2000 => WindowsIntegrityLevel.Medium,
            0x3000 => WindowsIntegrityLevel.High,
            0x4000 => WindowsIntegrityLevel.System,
            _ => WindowsIntegrityLevel.Unknown,
        };
    }

    public static string DescribeLowIntegrity(
        string executablePath,
        WindowsIntegrityLevel executable,
        WindowsIntegrityLevel process)
    {
        var where = executable switch
        {
            WindowsIntegrityLevel.None => "未设置标签，会跟随启动器",
            WindowsIntegrityLevel.Unknown => "标签未知（读取失败）",
            _ => executable.ToString(),
        };

        // 走到这里说明启动器自己就是 Low：提升 java.exe 没有意义（min(令牌, 标签) 仍是 Low），
        // 只能换一个 Medium 的宿主，或者把启动器 exe 本身的标签提上去。
        var launcher = Environment.ProcessPath ?? "启动器";

        return "启动器自身以低完整性级别运行（" + process + "），Java（" + where + "，" +
               executablePath + "）也会随之降到 " + EffectiveLevel(executable, process) + "。" +
               "Fabric/Quilt 会在这里失败：JDK 的 Files.isWritable 在低完整性进程里对所有文件都返回 false，" +
               "zipfs 把重映射产物当成只读，tinyremapper 报 \"jar can't be written\"。" +
               "修复：用 launch.cmd（dotnet 宿主，Medium）启动；或执行 " +
               "icacls \"" + launcher + "\" /setintegritylevel Medium 后重启启动器。";
    }

    private static string DescribeRaiseFailure(
        string executablePath,
        WindowsIntegrityLevel executable,
        string? error)
        => "无法把 Java 运行时的完整性标签从 " + executable + " 提升到 Medium（" +
           (string.IsNullOrWhiteSpace(error) ? "未知原因" : error) +
           "）。Fabric/Quilt 重映射会失败。请手动执行：" +
           "icacls \"" + executablePath + "\" /setintegritylevel Medium";

    /// <summary>把文件的完整性标签设为指定级别（含 NO_WRITE_UP，与 icacls /setintegritylevel 一致）。</summary>
    public static bool TryRaiseFileLevel(
        string path,
        WindowsIntegrityLevel target,
        out string? error)
    {
        error = null;

        var sidString = "S-1-16-" + ((int)target).ToString(System.Globalization.CultureInfo.InvariantCulture);
        IntPtr sid = IntPtr.Zero;
        IntPtr acl = IntPtr.Zero;
        IntPtr sd = IntPtr.Zero;

        try
        {
            if (!ConvertStringSidToSidW(sidString, out sid))
            {
                error = "ConvertStringSidToSidW: " + Marshal.GetLastWin32Error();
                return false;
            }

            // 一个 mandatory label ACE 很小；256 字节足够，同时留出对齐余量。
            acl = Marshal.AllocHGlobal(256);
            sd = Marshal.AllocHGlobal(64);

            if (!InitializeAcl(acl, 256, AclRevision))
            {
                error = "InitializeAcl: " + Marshal.GetLastWin32Error();
                return false;
            }

            if (!AddMandatoryAce(acl, AclRevision, 0, SystemMandatoryLabelNoWriteUp, sid))
            {
                error = "AddMandatoryAce: " + Marshal.GetLastWin32Error();
                return false;
            }

            if (!InitializeSecurityDescriptor(sd, 1))
            {
                error = "InitializeSecurityDescriptor: " + Marshal.GetLastWin32Error();
                return false;
            }

            if (!SetSecurityDescriptorSacl(sd, true, acl, false))
            {
                error = "SetSecurityDescriptorSacl: " + Marshal.GetLastWin32Error();
                return false;
            }

            if (!SetFileSecurityW(path, LabelSecurityInformation, sd))
            {
                error = "SetFileSecurity: " + Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }
        finally
        {
            if (sid != IntPtr.Zero) LocalFree(sid);
            if (acl != IntPtr.Zero) Marshal.FreeHGlobal(acl);
            if (sd != IntPtr.Zero) Marshal.FreeHGlobal(sd);
        }
    }

    private static WindowsIntegrityLevel? TryGetProcessLevel()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                return null;

            const int size = 256;
            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                return null;

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes } }
            var sid = Marshal.ReadIntPtr(buffer);
            return sid == IntPtr.Zero ? null : LevelFromSid(sid);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static WindowsIntegrityLevel? TryGetFileLevel(string path)
    {
        IntPtr buffer = IntPtr.Zero;

        try
        {
            // 1) 先问需要多大（预期 ERROR_INSUFFICIENT_BUFFER）。
            GetFileSecurityW(path, LabelSecurityInformation, IntPtr.Zero, 0, out var needed);
            if (needed == 0) return WindowsIntegrityLevel.None;

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetFileSecurityW(path, LabelSecurityInformation, buffer, needed, out _))
                return null;

            if (!GetSecurityDescriptorSacl(buffer, out var present, out var sacl, out _) ||
                !present || sacl == IntPtr.Zero)
            {
                return WindowsIntegrityLevel.None;
            }

            var aceCount = Marshal.ReadInt16(sacl, 4);
            for (var i = 0; i < aceCount; i++)
            {
                if (!GetAce(sacl, i, out var ace)) continue;
                if (Marshal.ReadByte(ace) != MandatoryLabelAceType) continue;

                // ACE_HEADER(4) + ACCESS_MASK(4) 之后是标签 SID。
                var sid = ace + 8;
                return LevelFromSid(sid);
            }

            return WindowsIntegrityLevel.None;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowsIntegrityLevel LevelFromSid(IntPtr sid)
    {
        try
        {
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) return WindowsIntegrityLevel.Unknown;

            var last = GetSidSubAuthority(sid, count - 1);
            var rid = (uint)Marshal.ReadInt32(last);

            return rid switch
            {
                0x0000 => WindowsIntegrityLevel.Untrusted,
                0x1000 => WindowsIntegrityLevel.Low,
                0x2000 => WindowsIntegrityLevel.Medium,
                0x3000 => WindowsIntegrityLevel.High,
                0x4000 => WindowsIntegrityLevel.System,
                _ => WindowsIntegrityLevel.Unknown,
            };
        }
        catch
        {
            return WindowsIntegrityLevel.Unknown;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, IntPtr information, int length, out int returnedLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetFileSecurityW(
        string name, uint securityInformation, IntPtr securityDescriptor, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor, out bool present, out IntPtr sacl, out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAce(IntPtr acl, int index, out IntPtr ace);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool InitializeAcl(IntPtr acl, int size, int revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AddMandatoryAce(
        IntPtr acl, int revision, int aceFlags, uint mandatoryPolicy, IntPtr labelSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool InitializeSecurityDescriptor(IntPtr securityDescriptor, int revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetSecurityDescriptorSacl(
        IntPtr securityDescriptor, bool present, IntPtr sacl, bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFileSecurityW(string name, uint securityInformation, IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
}
