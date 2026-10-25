using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

/// <summary>当前进程的执行上下文信息，用于排查"启动外部程序被拒"这类问题。</summary>
internal static class ProcessContext
{
    // whoami 是一次进程启动，缓存起来避免每次判断都付出代价
    private static readonly Lazy<string> CachedIntegrity = new(ReadIntegrityLabelCore);

    /// <summary>完整性标签，只保留 SID（形如 S-1-16-8192）。</summary>
    public static string IntegrityLabel => CachedIntegrity.Value;

    /// <summary>
    /// 是否处于 Low 完整性。
    ///
    /// Low 完整性会同时导致：
    ///   - 跨进程 shell COM 被拒（SHOpenFolderAndSelectItems 返回 0x80070005）
    ///   - ShellExecute 被拒（"拒绝访问"）
    ///   - explorer.exe 启动失败（0xC0000142 STATUS_DLL_INIT_FAILED）
    /// 也就是说，在这种进程里"打开文件夹"无论如何都做不到，应该直接告诉用户原因，
    /// 而不是把三条失败信息堆在状态栏里。
    /// </summary>
    public static bool IsLowIntegrity =>
        IntegrityLabel.Contains("S-1-16-4096", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读取强制完整性标签（Mandatory Integrity Label）。
    ///
    /// 只看 SID，因为文字描述随系统语言变化：
    ///   S-1-16-4096  = Low    —— 受限，无法调起资源管理器
    ///   S-1-16-8192  = Medium —— 普通用户程序
    ///   S-1-16-12288 = High   —— 已提权
    /// </summary>
    private static string ReadIntegrityLabelCore()
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
                if (!line.Contains("S-1-16-", StringComparison.OrdinalIgnoreCase)) continue;

                // 只保留 SID 部分，去掉本地化的标签文字，避免乱码
                var trimmed = line.Trim();
                var index = trimmed.IndexOf("S-1-16-", StringComparison.OrdinalIgnoreCase);
                return trimmed[index..].Trim();
            }

            return "(未找到强制标签行)";
        }
        catch (Exception ex)
        {
            return $"(读取失败: {ex.GetBaseException().Message})";
        }
    }
}
