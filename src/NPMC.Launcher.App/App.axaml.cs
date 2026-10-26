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
            // 正常双击启动时 desktop.Args 可能是 null（没有命令行参数），
            // 直接传给 MemoryProbe 的 args.Any(...) 会 NRE，窗口就永远不出现。
            var args = desktop.Args ?? Array.Empty<string>();

            var root = LauncherRoot.Resolve(args);
            var window = new MainWindow(root);
            desktop.MainWindow = window;

            // 内存诊断：走真实 Avalonia 平台（否则 Bitmap 无法解码），
            // 最小化窗口后按脚本切换实例/页签，采集快照并退出。
            if (MemoryProbe.IsRequested(args))
            {
                // 默认最小化（不打扰桌面）；--memory-visible 时正常显示，
                // 用于核对列表真的只给可见行加载图标。
                if (!MemoryProbe.WantsVisibleWindow(args))
                {
                    window.ShowInTaskbar = false;
                    window.WindowState = WindowState.Minimized;
                }

                Dispatcher.UIThread.Post(async () => await MemoryProbe.RunAsync(window, desktop, root, args));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
