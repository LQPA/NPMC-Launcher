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

            // 目录：允许继承给以后新建的 .bin
            var directoryOk = Apply(directory, userSid, inheritToChildren: true);

            // 已有文件必须单独处理：
            // icacls /T + (OI)(CI) 授权会把 ACE 标成 "仅继承"，对文件本身不生效，
            // 结果文件没有任何 Allow 规则（连当前用户都读不了）——这次真踩到了。
            foreach (var file in Directory.EnumerateFiles(directory))
                Apply(file, userSid, inheritToChildren: false);

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

            return directoryOk;
        }
        catch
        {
            return false;
        }
    }

    private static bool Apply(string path, string userSid, bool inheritToChildren)
    {
        var suffix = inheritToChildren ? ":(OI)(CI)F" : ":F";

        var start = new ProcessStartInfo("icacls")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add(path);
        start.ArgumentList.Add("/inheritance:r");
        start.ArgumentList.Add("/remove:g");

        foreach (var sid in BroadSids)
            start.ArgumentList.Add(sid);

        start.ArgumentList.Add("/grant:r");
        start.ArgumentList.Add($"*{userSid}{suffix}");

        foreach (var sid in WellKnownSids)
            start.ArgumentList.Add($"{sid}{suffix}");

        start.ArgumentList.Add("/C");
        start.ArgumentList.Add("/Q");

        using var process = Process.Start(start);
        if (process is null) return false;

        if (!process.WaitForExit(8000))
        {
            try { process.Kill(true); } catch { }
            return false;
        }

        return process.ExitCode == 0;
    }
}