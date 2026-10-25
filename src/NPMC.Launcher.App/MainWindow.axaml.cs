using Avalonia.Controls;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var paths = new LauncherPaths(AppContext.BaseDirectory);

        SubtitleBlock.Text = $"Avalonia 11.3.2 · .NET {Environment.Version}";
        RootBlock.Text = $"启动器根目录  {paths.Root}";
        RuntimeBlock.Text = $"共享资源目录  {paths.SharedRoot}";
    }
}
