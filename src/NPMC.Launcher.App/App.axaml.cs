using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace NPMC.Launcher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = LauncherRoot.Resolve(desktop.Args);
            desktop.MainWindow = new MainWindow(root);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
