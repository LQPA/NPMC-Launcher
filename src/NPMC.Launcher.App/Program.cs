using Avalonia;

namespace NPMC.Launcher.App;

internal static class Program
{
    // Avalonia 需要 STA 线程；且不能加 async（会丢失 STA）
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Avalonia 设计器会按名字找这个方法，签名不要改。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
