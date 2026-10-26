namespace NPMC.Launcher.App;

/// <summary>
/// 给窗口标题带一个构建时间。
///
/// 为什么需要：排查内存时反复出现"用户开的其实是上一个进程/旧 exe"，
/// 但界面上没有任何可区分的标记。标题里带发布/构建时间，截图就能直接对上是哪一版。
/// 单文件发布取 exe 的修改时间；dotnet 宿主调试时取程序集文件时间。
/// </summary>
internal static class BuildInfo
{
    public static string Stamp { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var process = Environment.ProcessPath;

            if (process is not null
                && Path.GetFileNameWithoutExtension(process)
                    .Equals("NPMC.Launcher.App", StringComparison.OrdinalIgnoreCase))
            {
                return File.GetLastWriteTime(process).ToString("MM-dd HH:mm");
            }

            var assembly = typeof(BuildInfo).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(assembly) && File.Exists(assembly))
                return File.GetLastWriteTime(assembly).ToString("MM-dd HH:mm");
        }
        catch
        {
            // 取不到就退回 dev，不影响启动
        }

        return "dev";
    }
}
