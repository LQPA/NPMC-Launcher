using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

internal sealed record FolderOpenAttempt(string Strategy, bool Success, string? Error);

/// <summary>
/// 打开目录的尝试链。依次尝试，成功即止，并把每次的结果都记下来。
///
/// 顺序的由来（实测）：
///   explorer.exe 在本机以 0xC0000142（STATUS_DLL_INIT_FAILED）失败，
///   所以先走 shell32（交给已有外壳，不新起进程），再退化到 shell 与 explorer.exe。
///
/// 判定成功的两个坑（都踩过）：
///   1. Process.Start 成功只代表进程被创建，不代表它没立刻死掉 → 要看退出码。
///   2. explorer 失败时会**停在系统错误对话框上不退出**。
///      若把"2 秒后仍在运行"当成成功，就会既误报"已打开"、又丢掉前面策略的真实错误，
///      而屏幕上还留着那个错误对话框。因此必须识别这种对话框，并按失败处理（顺手关掉它）。
/// </summary>
internal static class FolderLauncher
{
    public static async Task<IReadOnlyList<FolderOpenAttempt>> OpenAsync(string directory)
    {
        var attempts = new List<FolderOpenAttempt>(3);

        if (ShellFolder.TryOpen(directory, out var shell32Error))
        {
            attempts.Add(new FolderOpenAttempt("shell32", true, null));
            return attempts;
        }

        attempts.Add(new FolderOpenAttempt("shell32", false, shell32Error));

        // 把目录路径直接交给 shell 打开
        var shellError = await TryStartAsync(directory, null);
        attempts.Add(new FolderOpenAttempt("shell", shellError is null, shellError));
        if (shellError is null) return attempts;

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

            // explorer.exe 正常时会把请求转交给已有外壳后立刻退出（退出码 0 或 1）
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
