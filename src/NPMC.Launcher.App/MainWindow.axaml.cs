using Avalonia.Controls;
using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.App;

public partial class MainWindow : Window
{
    /// <summary>供 XAML 设计器使用；真实启动走带 root 的构造函数。</summary>
    public MainWindow() : this(AppContext.BaseDirectory)
    {
    }

    public MainWindow(string root)
    {
        InitializeComponent();

        var viewModel = new MainWindowViewModel(new LauncherPaths(root));
        DataContext = viewModel;

        // LoadAsync 内部已捕获全部异常并写进 Status，因此这里不需要额外的异常处理
        _ = viewModel.LoadAsync();
    }
}
