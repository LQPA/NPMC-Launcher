namespace NPMC.Launcher.App;

/// <summary>
/// 极简启动器日志：追加写入 &lt;root&gt;\logs\launcher.log。
///
/// 为什么需要：界面里的失败原因只显示在状态栏，容易被忽略、也不便复制。
/// 而"打开目录失败""启动失败"这类问题恰恰依赖现场信息才能定位。
/// 写进文件后，用户只要把文件贴出来即可。
/// 任何写日志的失败都必须静默忽略 —— 日志不能反过来影响主流程。
/// </summary>
internal static class LauncherLog
{
    public static void Write(string root, string message)
    {
        try
        {
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "launcher.log"), line);
        }
        catch
        {
            // 日志写入失败不影响功能
        }
    }
}
