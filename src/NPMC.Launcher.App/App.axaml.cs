using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace NPMC.Launcher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = LauncherRoot.Resolve(desktop.Args);
            var window = new MainWindow(root);
            desktop.MainWindow = window;

            // 内存诊断：走真实 Avalonia 平台（否则 Bitmap 无法解码），
            // 最小化窗口后按脚本切换实例/页签，采集快照并退出。
            if (MemoryProbe.IsRequested(desktop.Args))
            {
                // 默认最小化（不打扰桌面）；--memory-visible 时正常显示，
                // 用于核对列表真的只给可见行加载图标。
                if (!MemoryProbe.WantsVisibleWindow(desktop.Args))
                {
                    window.ShowInTaskbar = false;
                    window.WindowState = WindowState.Minimized;
                }

                Dispatcher.UIThread.Post(async () => await MemoryProbe.RunAsync(window, desktop, root, desktop.Args));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
