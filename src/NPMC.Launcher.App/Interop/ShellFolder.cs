using System.Runtime.InteropServices;

namespace NPMC.Launcher.App.Interop;

/// <summary>
/// 通过 shell32 让**已经在运行**的资源管理器打开目录。
///
/// 为什么需要它：
///   直接 <c>Process.Start("explorer.exe")</c> 是<b>新起一个 explorer 进程</b>。
///   实测在本机会失败并弹出系统对话框「应用程序无法正常启动(0xC0000142)」，
///   即 STATUS_DLL_INIT_FAILED —— 那是 explorer 进程自身的 DLL 初始化失败。
///   而 SHOpenFolderAndSelectItems 是把请求交给现有外壳（COM，走已有的 explorer），
///   不需要新进程，因此不受该问题影响。
///
/// 注意：必须在 STA 线程调用（Avalonia 的 UI 线程就是 STA）。
/// </summary>
internal static class ShellFolder
{
    // 这两个都是 Unicode-only 且导出名不带 A/W 后缀，因此显式 ExactSpelling，
    // 避免 CLR 先去找 ...W 再回退带来的不确定性。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    public static bool TryOpen(string directory, out string error)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var hr = SHParseDisplayName(directory, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                error = $"SHParseDisplayName 0x{hr:X8}";
                return false;
            }

            hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
            if (hr != 0)
            {
                error = $"SHOpenFolderAndSelectItems 0x{hr:X8}";
                return false;
            }

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }
}
