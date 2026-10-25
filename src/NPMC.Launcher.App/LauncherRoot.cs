namespace NPMC.Launcher.App;

/// <summary>
/// 启动器根目录的解析。
///
/// 优先级：
///   1. 命令行 --root &lt;目录&gt;
///   2. 环境变量 NPMC_ROOT
///   3. 可执行文件所在目录（portable 语义：把 exe 拷到哪，数据就在哪）
///
/// 必须显式可配，因为界面程序和命令行工具的数据目录经常不是同一个：
/// 开发时 exe 在 bin/Release/net8.0 下，而真实数据在仓库根的 data/ 里。
/// </summary>
internal static class LauncherRoot
{
    public const string EnvironmentVariable = "NPMC_ROOT";

    public static string Resolve(string[]? args)
    {
        if (args is not null)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(args[i + 1]);
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        return AppContext.BaseDirectory;
    }
}
