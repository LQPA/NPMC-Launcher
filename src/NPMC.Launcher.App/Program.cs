using Avalonia;

namespace NPMC.Launcher.App;

internal static class Program
{
    // Avalonia 需要 STA 线程；且不能加 async（会丢失 STA）
    [STAThread]
    public static void Main(string[] args)
    {
        // 诊断模式：不开窗口，把界面模型算出来的内容写成文本，方便在无图形环境下核对
        if (InstanceDump.TryRun(args)) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Avalonia 设计器会按名字找这个方法，签名不要改。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
