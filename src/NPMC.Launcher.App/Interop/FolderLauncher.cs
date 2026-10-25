using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

internal sealed record FolderOpenAttempt(string Strategy, bool Success, string? Error);

/// <summary>
/// 打开目录的尝试链。依次尝试，成功即止，并把每次的结果都记下来。
///
/// 为什么**默认不启动 explorer.exe**：
///   在这台机器上 explorer.exe 启动即失败（0xC0000142 STATUS_DLL_INIT_FAILED），
///   而且它失败时会**弹出一个系统错误对话框**，用户必须手动关掉。
///   一个"打开文件夹"按钮每次点都弹出这种对话框是不可接受的，
///   所以正式流程只用不会打弹窗的两种方式：
///     1. shell32 —— 交给已在运行的资源管理器，不新起进程（首选）
///     2. shell   —— 让系统按默认关联打开目录
///   两者都失败时由上层把路径复制到剪贴板。
///   explorer.exe 只留给诊断命令显式开启（--all），便于以后排查。
/// </summary>
internal static class FolderLauncher
{
    public static async Task<IReadOnlyList<FolderOpenAttempt>> OpenAsync(
        string directory,
        bool allowProcessLaunch = true,
        bool allowExplorer = false)
    {
        var attempts = new List<FolderOpenAttempt>(3);

        if (ShellFolder.TryOpen(directory, out var shell32Error))
        {
            attempts.Add(new FolderOpenAttempt("shell32", true, null));
            return attempts;
        }

        attempts.Add(new FolderOpenAttempt("shell32", false, shell32Error));

        if (!allowProcessLaunch) return attempts;

        // 把目录路径直接交给 shell 打开（失败只会抛异常，不会弹窗）
        var shellError = await TryStartAsync(directory, null);
        attempts.Add(new FolderOpenAttempt("shell", shellError is null, shellError));
        if (shellError is null) return attempts;

        if (!allowExplorer) return attempts;

        var explorerError = await TryStartAsync("explorer.exe", $"\"{directory}\"");
        attempts.Add(new FolderOpenAttempt("explorer", explorerError is null, explorerError));

        return attempts;
    }

    /// <summary>启动并判断是否真的成功：返回 null 表示成功，否则返回失败原因。</summary>
    private static async Task<string?> TryStartAsync(string fileName, string? arguments)
    {
        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };
            if (arguments is not null) startInfo.Arguments = arguments;

            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }

        if (process is null) return "启动后未返回进程";

        try
        {
            var exited = true;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }
            catch
            {
                return null;   // 拿不到退出状态（受限环境常见），按成功处理
            }

            if (!exited)
            {
                // 停在系统错误对话框 = 失败，而且要把对话框关掉
                if (HasErrorDialog(process))
                {
                    var title = SafeTitle(process);
                    try { process.Kill(); } catch { /* 已经退出就算了 */ }
                    return $"停在系统错误对话框（{title}）";
                }

                return null;   // 还活着且有窗口 → 认为已经打开
            }

            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { return null; }

            // 正常时会把请求转交给已有外壳后立刻退出（退出码 0 或 1）
            return exitCode is 0 or 1 ? null : $"退出码 0x{exitCode:X8}";
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? SafeTitle(Process process)
    {
        try { return process.MainWindowTitle; }
        catch { return null; }
    }

    /// <summary>
    /// 识别 Windows 的硬错误对话框（进程启动失败时弹出，标题形如
    /// "explorer.exe - Application Error"）。中英文都覆盖。
    /// </summary>
    private static bool HasErrorDialog(Process process)
    {
        var title = SafeTitle(process);
        if (string.IsNullOrEmpty(title)) return false;

        return title.Contains("Application Error", StringComparison.OrdinalIgnoreCase)
            || title.Contains("应用程序错误", StringComparison.Ordinal);
    }
}
