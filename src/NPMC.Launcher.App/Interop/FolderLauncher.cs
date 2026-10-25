using System.Diagnostics;

namespace NPMC.Launcher.App.Interop;

internal sealed record FolderOpenAttempt(string Strategy, bool Success, string? Error);

/// <summary>
/// 打开目录的尝试链。依次尝试，成功即止，并把每次的结果都记下来。
///
/// 顺序的由来（实测）：
///   explorer.exe 在本机以 0xC0000142（STATUS_DLL_INIT_FAILED）退出，
///   也就是"新起一个 explorer 进程"这条路本身就是坏的。
///   所以先走 shell32（交给已有外壳，不新起进程），再退化到 shell 与 explorer.exe。
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

    /// <summary>
    /// 启动并判断是否真的成功：返回 null 表示成功，否则返回失败原因。
    ///
    /// 关键点：Process.Start 成功**只代表进程被创建**，不代表它没立刻死掉。
    /// 实测 explorer.exe 以 0xC0000142 退出时，旧实现会误判成功并弹出一个系统错误对话框，
    /// 兜底逻辑永远不会执行。因此这里要等一小会儿并检查退出码。
    /// </summary>
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return null;   // 一秒半还没退出 → 窗口已经开出来了
            }
            catch
            {
                return null;   // 拿不到退出状态（受限环境常见），按成功处理
            }

            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { return null; }

            // explorer.exe 正常时会把请求转交给已有外壳后退出（退出码 0 或 1）
            return exitCode is 0 or 1 ? null : $"退出码 0x{exitCode:X8}";
        }
        finally
        {
            process.Dispose();
        }
    }
}
