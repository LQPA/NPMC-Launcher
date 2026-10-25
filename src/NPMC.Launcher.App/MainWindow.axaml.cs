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

        var viewModel = new MainWindowViewModel(new LauncherPaths(root))
        {
            // 打开目录的三条路都失败时，把路径写进剪贴板作为兜底。
            // 剪贴板必须由窗口（TopLevel）提供，所以在这里注入而不是在 ViewModel 里直接取。
            CopyToClipboard = async text =>
            {
                var clipboard = Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(text);
            },
        };

        DataContext = viewModel;

        // LoadAsync 内部已捕获全部异常并写进 Status，因此这里不需要额外的异常处理
        _ = viewModel.LoadAsync();
    }
}
