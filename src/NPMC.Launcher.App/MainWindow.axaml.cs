using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    /// <summary>
    /// 选择本地整合包。文件对话框必须由窗口提供，所以放在代码里而不是 ViewModel。
    /// </summary>
    private async void OnPickModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择整合包",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Modrinth 整合包") { Patterns = new[] { "*.mrpack" } },
                    new FilePickerFileType("压缩包") { Patterns = new[] { "*.zip" } },
                },
            });

            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) await viewModel.LoadModpackFileAsync(path);
        }
        catch (Exception ex)
        {
            viewModel.ReportModpackFailure($"无法打开文件选择器：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>手动指定 java.exe（非常规安装路径）。</summary>
    private async void OnPickJavaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 java.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Java 可执行文件") { Patterns = new[] { "java.exe", "javaw.exe" } },
                },
            });

            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) viewModel.SetCustomJava(path);
        }
        catch (Exception ex)
        {
            viewModel.ReportModpackFailure($"选择 Java 失败：{ex.GetBaseException().Message}");
        }
    }
    /// <summary>拖拽悬停：只有带文件时才显示"可放置"。</summary>
    private void OnModpackDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>拖入文件：取第一个 .mrpack。</summary>
    private async void OnModpackDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (!e.Data.Contains(DataFormats.Files)) return;

        var path = e.Data.GetFiles()?
            .Select(item => item.TryGetLocalPath())
            .FirstOrDefault(local => local is not null
                                     && local.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(path))
        {
            viewModel.ReportModpackFailure("拖入的文件不是 .mrpack");
        }
        else
        {
            await viewModel.LoadModpackFileAsync(path);
        }

        e.Handled = true;
    }
}
