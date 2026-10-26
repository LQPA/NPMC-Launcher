using Avalonia;

namespace NPMC.Launcher.App;

internal static class Program
{
    // Avalonia 需要 STA 线程；且不能加 async（会丢失 STA）
    [STAThread]
    public static void Main(string[] args)
    {
        // 诊断模式：不开窗口，把界面模型算出来的内容写成文本，方便在无图形环境下核对
        // 注意：这里不初始化 Avalonia 渲染平台 —— SetupWithoutStarting 会阻塞，
        // 因此诊断里拿不到 Bitmap（图标会显示为未加载），真实窗口不受影响。
        if (InstanceDump.TryRun(args)) return;

        // 诊断模式：验证"打开目录"哪条路可用
        if (FolderOpenTest.TryRun(args)) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Avalonia 设计器会按名字找这个方法，签名不要改。</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // 默认软件渲染：
        // GPU 渲染会给每张图标位图建纹理，滚一遍 495 个模组时工作集能到 500MB+
        // （实测 GPU 515MB / 软件渲染 174MB，图标像素其实只有 4.6MB）。
        // 启动器界面很简单，软件渲染完全够用；需要对比 GPU 时设 NPMC_GPU_RENDERING=1。
        var forceGpu = Environment.GetEnvironmentVariable("NPMC_GPU_RENDERING") is "1" or "true" or "TRUE";

        if (!forceGpu)
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
            });
        }

        return builder;
    }
}
