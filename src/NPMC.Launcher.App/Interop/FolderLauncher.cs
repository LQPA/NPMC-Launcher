using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

internal sealed record FolderOpenAttempt(string Strategy, bool Success, string? Error);

/// <summary>
/// 打开目录的尝试链。
///
/// 顺序与理由：
///   1. **shell（把目录路径交给 ShellExecute）** —— 唯一"进入该目录内部"的标准做法。
///      成败只看 `Process.Start` 有没有抛异常：**不要再去等进程退出码**。
///      外壳会把请求转交给已经在运行的 explorer，退出码不可靠；
///      一旦把"其实已经成功"误判为失败，就会继续走下一策略，结果**同时开出两个窗口**。
///   2. shell32 —— 交给已运行的外壳，不新起进程。注意它对 cidl=0 的表现是
///      "打开父目录并定位该文件夹"，所以只能兜底，不能首选。
///   3. explorer.exe —— **默认不启用**：本机启动即失败（0xC0000142）且会弹出
///      系统错误对话框。只有诊断命令 --all 才尝试，并会在识别到错误对话框时关掉它。
///
/// 全部失败时由上层把路径复制到剪贴板。
/// </summary>
internal static class FolderLauncher
{
    public static async Task<IReadOnlyList<FolderOpenAttempt>> OpenAsync(
        string directory,
        bool allowProcessLaunch = true,
        bool allowExplorer = false)
    {
        var attempts = new List<FolderOpenAttempt>(3);

        if (allowProcessLaunch)
        {
            // 首选：直接打开目录内部。抛异常才算失败，成功即结束。
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });

                attempts.Add(new FolderOpenAttempt("shell", true, null));
                return attempts;
            }
            catch (Exception ex)
            {
                attempts.Add(new FolderOpenAttempt("shell", false, ex.GetBaseException().Message));
            }
        }

        // 兜底：请已在运行的外壳打开（可能表现为打开父目录并选中）
        if (ShellFolder.TryOpen(directory, out var shell32Error))
        {
            attempts.Add(new FolderOpenAttempt("shell32", true, null));
            return attempts;
        }

        attempts.Add(new FolderOpenAttempt("shell32", false, shell32Error));

        if (!allowExplorer) return attempts;

        var explorerError = await TryStartExplorerAsync($"\"{directory}\"");
        attempts.Add(new FolderOpenAttempt("explorer", explorerError is null, explorerError));

        return attempts;
    }

    /// <summary>
    /// 启动 explorer.exe 并判断是否真的成功：返回 null 表示成功，否则返回失败原因。
    ///
    /// 仅用于诊断。这里必须检查退出码与错误对话框：
    ///   失败时 explorer 会以 0xC0000142 退出，或者**停在系统错误对话框上不退出**
    ///   （后者要把进程关掉，否则弹窗会一直留在屏幕上）。
    /// </summary>
    private static async Task<string?> TryStartExplorerAsync(string arguments)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
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
                if (HasErrorDialog(process))
                {
                    var title = SafeTitle(process);
                    try { process.Kill(); } catch { /* 已经退出就算了 */ }
                    return $"停在系统错误对话框（{title}）";
                }

                return null;
            }

            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { return null; }

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

    /// <summary>识别 Windows 的硬错误对话框（标题形如 "explorer.exe - Application Error"）。</summary>
    private static bool HasErrorDialog(Process process)
    {
        var title = SafeTitle(process);
        if (string.IsNullOrEmpty(title)) return false;

        return title.Contains("Application Error", StringComparison.OrdinalIgnoreCase)
            || title.Contains("应用程序错误", StringComparison.Ordinal);
    }
}
