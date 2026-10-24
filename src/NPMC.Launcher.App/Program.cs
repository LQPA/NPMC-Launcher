namespace NPMC.Launcher.App;

/// <summary>
/// 前端占位入口。
///
/// 正式的 Avalonia 应用会把它替换为 App.axaml + Program.BuildAvaloniaApp()，
/// 接入所需 NuGet 包（Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent、CommunityToolkit.Mvvm）。
///
/// 现在保留它有两个用处：
///   1. 让 NPMC.Launcher.App 编译通过，解决方案结构完整；
///   2. 在尚未接入 UI 时，提示正确的前置步骤而不是静默失败。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("NPMC Launcher —— UI 尚未接入。");
        Console.WriteLine();
        Console.WriteLine("当前可用的功能在命令行工具里：");
        Console.WriteLine("  npmc probe   1.20.1 --root .\\data   组装启动参数");
        Console.WriteLine("  npmc install 1.20.1 --root .\\data   下载 client jar / libraries");
        Console.WriteLine("  npmc runtime java-runtime-gamma      安装内置 Java");
        Console.WriteLine("  npmc launch  1.20.1 --root .\\data   启动游戏");
        Console.WriteLine();
        Console.WriteLine("接入 Avalonia 需要联网执行 restore 拉取：");
        Console.WriteLine("  Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent / CommunityToolkit.Mvvm");
        return 0;
    }
}
