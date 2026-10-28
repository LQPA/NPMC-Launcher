using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NPMC.Launcher.Infrastructure.Launch;

/// <summary>
/// 把游戏窗口标题改成用户设定的文字（实例「高级选项 → 自定义游戏标题」）。
///
/// 为什么用 Win32：窗口标题是游戏自己通过 LWJGL 的 Display.setTitle 设的，
/// 没有任何 JVM / 游戏参数能改它；要对所有版本生效只能从外部改。
/// 游戏在加载世界、切窗口时会重置标题，所以运行期间每秒重新应用一次。
/// 非 Windows 上直接跳过（本项目只发布 Windows）。
/// </summary>
public static class GameWindowTitle
{
    private const int PollMilliseconds = 1000;

    /// <summary>启动一个后台轮询，把该进程的所有可见顶层窗口标题改成 <paramref name="title"/>。</summary>
    public static void Start(Process process, string title)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(title)) return;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (process.HasExited) return;
                }
                catch
                {
                    return;
                }

                Apply(process.Id, title);

                try
                {
                    await Task.Delay(PollMilliseconds).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
            }
        });
    }

    private static void Apply(int processId, string title)
    {
        try
        {
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != processId) return true;
                if (!IsWindowVisible(window)) return true;

                SetWindowText(window, title);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 改不了标题不影响游戏
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr window, string text);
}
