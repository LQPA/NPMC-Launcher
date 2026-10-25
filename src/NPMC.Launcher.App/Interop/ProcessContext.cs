using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

/// <summary>当前进程的执行上下文信息，用于排查"启动外部程序被拒"这类问题。</summary>
internal static class ProcessContext
{
    /// <summary>
    /// 读取强制完整性标签（Mandatory Integrity Label）。
    ///
    /// 只看 SID，因为文字描述随系统语言变化：
    ///   S-1-16-4096  = Low    —— 会被拒绝跨进程 COM、ShellExecute、以及 explorer 启动
    ///   S-1-16-8192  = Medium —— 普通用户程序
    ///   S-1-16-12288 = High   —— 已提权
    /// </summary>
    public static string ReadIntegrityLabel()
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
                {
                    // 只保留 SID 与级别名，去掉本地化的标签文字，避免乱码
                    var sid = line.Trim();
                    var index = sid.IndexOf("S-1-16-", StringComparison.OrdinalIgnoreCase);
                    return sid[index..].Trim();
                }
            }

            return "(未找到强制标签行)";
        }
        catch (Exception ex)
        {
            return $"(读取失败: {ex.GetBaseException().Message})";
        }
    }
}
