using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

internal sealed record FolderOpenAttempt(string Strategy, bool Success, string? Error);

/// <summary>
/// 打开目录的尝试链。依次尝试，成功即止，并把每次的结果都记下来。
///
/// 顺序与理由：
///   1. **shell（把目录路径交给 ShellExecute）** —— 这是唯一"进入该目录内部"
///      的标准做法。注意 shell32 那条路（SHOpenFolderAndSelectItems，cidl=0）
///      的实际表现是打开**父目录并定位**到该文件夹，用户会觉得"没进去"，
///      所以它只能做兜底而不是首选。
///   2. shell32 —— 交给已运行的外壳，不新起进程；在 shell 失败时仍值得一试。
///   3. explorer.exe —— **默认不启用**：在这台机器上它启动即失败
///      （0xC0000142）并弹出系统错误对话框，每次点击都留一个弹窗不可接受。
///      仅诊断命令 --all 会尝试。
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
            // 首选：直接把目录交给 shell，打开的是"目录内部"
            var shellError = await TryStartAsync(directory, null);
            attempts.Add(new FolderOpenAttempt("shell", shellError is null, shellError));
            if (shellError is null) return attempts;
        }

        // 兜底：请已在运行的外壳打开（可能表现为打开父目录并选中）
        if (ShellFolder.TryOpen(directory, out var shell32Error))
        {
            attempts.Add(new FolderOpenAttempt("shell32", true, null));
            return attempts;
        }

        attempts.Add(new FolderOpenAttempt("shell32", false, shell32Error));

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
