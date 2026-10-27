namespace NPMC.Launcher.App;

/// <summary>
/// 启动器根目录的解析。
///
/// 优先级：
///   1. 命令行 --root &lt;目录&gt;
///   2. 环境变量 NPMC_ROOT
///   3. **上次用过的目录**（记录在 %APPDATA%\NPMC-Launcher\last-root.txt）
///   4. 可执行文件所在目录（portable 语义）
///
/// 为什么需要第 3 条：双击图标启动时没有命令行参数，也没有环境变量，
/// 若直接回落到 exe 目录，用户会看到一个空实例列表 —— 看起来像"数据丢了"。
/// 记住上次目录后，双击也能回到原来的数据。
/// </summary>
internal static class LauncherRoot
{
    public const string EnvironmentVariable = "NPMC_ROOT";

    private static string ReminderFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NPMC-Launcher",
        "last-root.txt");

    public static string Resolve(string[]? args)
    {
        // 1) --root
        if (args is not null)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase))
                {
                    var fromArguments = Path.GetFullPath(args[i + 1]);
                    Remember(fromArguments);
                    return fromArguments;
                }
            }
        }

        // 2) 环境变量
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var fromVariable = Path.GetFullPath(fromEnvironment);
            Remember(fromVariable);
            return fromVariable;
        }

        // 3) exe 旁边已经有便携布局（data/minecraft）→ 以当前目录为准。
        //    这样把 exe + data + minecraft 一起复制到别的盘/目录时，不会继续用
        //    last-root 里记录的旧 D 盘路径。
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (Directory.Exists(Path.Combine(executableDirectory, "data"))
            || Directory.Exists(Path.Combine(executableDirectory, "minecraft")))
        {
            return executableDirectory;
        }

        // 4) 上次用过的目录（要求仍然存在，避免记住了一个已删除的路径）
        var remembered = Recall();
        if (remembered is not null) return remembered;

        // 5) exe 所在目录
        return executableDirectory;
    }

    private static void Remember(string root)
    {
        try
        {
            var file = ReminderFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, root);
        }
        catch
        {
            // 记不住不影响启动
        }
    }

    private static string? Recall()
    {
        try
        {
            var file = ReminderFile;
            if (!File.Exists(file)) return null;

            var text = File.ReadAllText(file).Trim();
            return text.Length > 0 && Directory.Exists(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
