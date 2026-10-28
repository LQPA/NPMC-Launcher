using System.Diagnostics;
using System.Security.Principal;

namespace NPMC.Launcher.Infrastructure.Accounts;

/// <summary>
/// 把 <c>microsoft-tokens</c> 目录收紧为“当前用户 + SYSTEM + Administrators”。
///
/// 为什么需要：端口版的数据目录继承 <c>data</c> 的 ACL，默认常含
/// <c>Authenticated Users:(M)</c> / <c>Users:(RX)</c>，本机其他用户虽然解不开 DPAPI 密文，
/// 但可以删除或替换 token 文件（迫使重新登录）。这里去掉继承，只留必要主体。
///
/// 用 icacls 而不是托管的 DirectorySecurity：后者需要额外的
/// System.IO.FileSystem.AccessControl 包，而 icacls 在所有支持的 Windows 上都有；
/// 失败时静默跳过，绝不因为收紧 ACL 而让登录失败。
/// </summary>
public static class TokenDirectoryAcl
{
    private static readonly string[] WellKnownSids =
    {
        "*S-1-5-18",      // NT AUTHORITY\SYSTEM
        "*S-1-5-32-544",  // BUILTIN\Administrators
    };

    /// <summary>要从 token 目录里删掉的“宽”主体：Everyone / Authenticated Users / Users 等。</summary>
    private static readonly string[] BroadSids =
    {
        "*S-1-1-0",       // Everyone
        "*S-1-5-11",      // NT AUTHORITY\Authenticated Users
        "*S-1-5-32-545",  // BUILTIN\Users
        "*S-1-5-32-546",  // BUILTIN\Guests
        "*S-1-5-4",       // NT AUTHORITY\INTERACTIVE
        "*S-1-5-7",       // NT AUTHORITY\ANONYMOUS LOGON
    };

    public static bool Protect(string directory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            Directory.CreateDirectory(directory);

            var userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(userSid)) return false;

            var start = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("/inheritance:r");   // 去掉从 data 继承来的宽 ACL

            // /inheritance:r 只处理“继承来的”ACE；如果目录此前已经被转成显式 ACE
            // （例如用 /inheritance:d 复制过），还得把 Everyone/Users 这些显式项删掉。
            start.ArgumentList.Add("/remove:g");
            foreach (var sid in BroadSids)
                start.ArgumentList.Add(sid);

            start.ArgumentList.Add("/grant:r");         // 只保留显式授权
            start.ArgumentList.Add($"*{userSid}:(OI)(CI)F");

            foreach (var sid in WellKnownSids)
                start.ArgumentList.Add($"{sid}:(OI)(CI)F");

            start.ArgumentList.Add("/T");               // 已有的 .bin 也一起收紧
            start.ArgumentList.Add("/C");               // 单个文件失败时继续
            start.ArgumentList.Add("/Q");

            using var process = Process.Start(start);
            if (process is null) return false;

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(true); } catch { }
                return false;
            }

            // 资源管理器里默认不显示（隐藏不是安全边界，只是少一点“误点/误拷”）
            try
            {
                var attributes = File.GetAttributes(directory);
                if (!attributes.HasFlag(FileAttributes.Hidden))
                    File.SetAttributes(directory, attributes | FileAttributes.Hidden);
            }
            catch
            {
                // 隐藏失败不影响 ACL
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
