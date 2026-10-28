using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.App;

public partial class MainWindow : Window
{
    /// <summary>供 XAML 设计器使用；真实启动走带 root 的构造函数。</summary>
    public MainWindow() : this(AppContext.BaseDirectory)
    {
    }

    public MainWindow(string root, bool portable = false)
    {
        InitializeComponent();

        // 截图里能直接看出是哪次构建，避免"开的是旧进程"这种误判
        Title = $"NPMC Launcher · {BuildInfo.Stamp}";

        // ListBoxItem 自己会在 PointerPressed 里选中并把事件标记为已处理，
        // 所以拖动排序要在隧道（Tunnel）阶段接管，否则收不到。
        InstanceList.AddHandler(
            InputElement.PointerPressedEvent, OnInstancePointerPressed, RoutingStrategies.Tunnel);
        InstanceList.AddHandler(
            InputElement.PointerMovedEvent, OnInstancePointerMoved, RoutingStrategies.Tunnel);
        InstanceList.AddHandler(
            InputElement.PointerReleasedEvent, OnInstancePointerReleased, RoutingStrategies.Tunnel);

        var paths = portable ? LauncherPaths.Portable(root) : new LauncherPaths(root);
        var viewModel = new MainWindowViewModel(paths)
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
        _loadTask = viewModel.LoadAsync();
    }

    private readonly Task _loadTask;

    /// <summary>诊断（--exercise-memory）要等首页加载完成再开始操作。</summary>
    internal Task LoadTask => _loadTask;

    internal MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    // ---- 实例列表：按住拖动排序 ----

    private Point _instanceDragStart;
    private InstanceListItem? _instanceDragCandidate;
    private bool _isDraggingInstance;

    private void OnInstancePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _instanceDragCandidate = (e.Source as Control)?.DataContext as InstanceListItem;
        _instanceDragStart = e.GetPosition(InstanceList);
        _isDraggingInstance = false;
    }

    private void OnInstancePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_instanceDragCandidate is not { } candidate) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _instanceDragCandidate = null;
            _isDraggingInstance = false;
            return;
        }

        var position = e.GetPosition(InstanceList);

        // 4px 阈值：避免"点一下选中"被当成拖动
        if (!_isDraggingInstance
            && Math.Abs(position.X - _instanceDragStart.X) < 4
            && Math.Abs(position.Y - _instanceDragStart.Y) < 4)
        {
            return;
        }

        _isDraggingInstance = true;

        // 不做 OLE 拖放：直接按指针落在哪一行就挪到哪一行（每一步都是实时的）。
        // 这样拖到一半松手也会停在当前位置，且不依赖系统拖放循环。
        var target = FindInstanceAt(position);
        if (target is null || target.Id == candidate.Id) return;

        Vm?.MoveInstance(candidate.Id, target.Id);
    }

    private void OnInstancePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _instanceDragCandidate = null;
        _isDraggingInstance = false;
    }

    /// <summary>指针下面的那一行（用可视树里的 ListBoxItem 命中，比按行高估算可靠）。</summary>
    private InstanceListItem? FindInstanceAt(Point position)
    {
        foreach (var item in InstanceList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var origin = item.TranslatePoint(new Point(0, 0), InstanceList);
            if (origin is null) continue;

            var bounds = new Rect(origin.Value, item.Bounds.Size);
            if (bounds.Contains(position) && item.DataContext is InstanceListItem instance)
                return instance;
        }

        return null;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>
    /// 页签选中项兜底。
    ///
    /// 不可用的页签（缺加载器 / 缺光影支持）由 ViewModel 拒绝切换，但 ListBox 自身已经把
    /// 选中项改过去了；而 ViewModel 那边"源值没变"，绑定不会回写目标值，界面上就留下
    /// "光影高亮、内容还是日志"的错位。这里在控件层把选中项拉回当前有效页签。
    /// </summary>
    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_revertingTabSelection) return;
        if (sender is not ListBox list || DataContext is not MainWindowViewModel viewModel) return;
        if (list.SelectedItem is not ContentTabItem { IsEnabled: false }) return;

        _revertingTabSelection = true;
        try
        {
            list.SelectedItem = viewModel.SelectedTab;
        }
        finally
        {
            _revertingTabSelection = false;
        }
    }

    private bool _revertingTabSelection;

    // ---- 内容列表：图标按"行真的出现"再加载 ----
    //
    // 495 行一次性解码 495 个位图（约 100~300MB）是内存尖峰的主因；
    // 列表本身是虚拟化的，所以只在行挂到可视树时取图标、离开时丢掉引用，
    // 滚动时由 IconLoader 的 LRU 缓存兜住最近用过的图标。

    // 行容器会被虚拟化面板复用：滚动时可能只换 DataContext，不触发 Detach。
    // 所以每个 Control 记住自己当前显示的是哪个 item，换人/离开时都要归还租约。
    // 用弱键表：面板若把容器放进回收池但不触发 Detach，也不会被这个字典钉住。
    private readonly ConditionalWeakTable<Control, ContentEntryItem> _contentRowItems = new();

    private void OnContentRowAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) TrackContentRow(control, control.DataContext as ContentEntryItem);
    }

    private void OnContentRowDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;

        if (_contentRowItems.TryGetValue(control, out var tracked))
        {
            _contentRowItems.Remove(control);
            Vm?.ReleaseIcon(tracked);
            return;
        }

        if (control.DataContext is ContentEntryItem item) Vm?.ReleaseIcon(item);
    }

    private void OnContentRowDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control) TrackContentRow(control, control.DataContext as ContentEntryItem);
    }

    private void TrackContentRow(Control control, ContentEntryItem? item)
    {
        if (_contentRowItems.TryGetValue(control, out var previous) && !ReferenceEquals(previous, item))
            Vm?.ReleaseIcon(previous);

        if (item is null)
        {
            _contentRowItems.Remove(control);
            return;
        }

        _contentRowItems.Remove(control);
        _contentRowItems.Add(control, item);
        item.IsRowVisible = true;
        if (Vm is { } viewModel) _ = viewModel.EnsureIconAsync(item);
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

    private async void OnExportInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (viewModel.SelectedInstance is not { } instance) return;

        var mrpack = viewModel.ExportFormatIndex == 1;

        try
        {
            var files = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = mrpack ? "导出 Modrinth 整合包" : "导出实例",
                SuggestedFileName = instance.Name + (mrpack ? ".mrpack" : ".zip"),
                DefaultExtension = mrpack ? "mrpack" : "zip",
                FileTypeChoices = mrpack
                    ? new[] { new FilePickerFileType("Modrinth 整合包") { Patterns = new[] { "*.mrpack" } } }
                    : new[] { new FilePickerFileType("ZIP 压缩包") { Patterns = new[] { "*.zip" } } },
            });

            var path = files?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            // 用户没补扩展名时自己补：.mrpack 没有关联程序，很容易只写出裸文件名
            if (mrpack && !path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) path += ".mrpack";
            if (!mrpack && !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) path += ".zip";

            if (mrpack) await viewModel.ExportMrpackAsync(path);
            else await viewModel.ExportInstanceAsync(path);
        }
        catch (Exception ex)
        {
            viewModel.ReportModpackFailure($"无法打开保存对话框：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>选择“手动下载的模组”所在目录（默认监听系统下载目录）。</summary>
    private async void OnPickBlockedFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择下载目录",
                AllowMultiple = false,
            });

            if (folders.Count == 0) return;

            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) viewModel.SetBlockedDownloadFolder(path);
        }
        catch (Exception ex)
        {
            viewModel.ReportModpackFailure($"无法打开目录选择器：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>设置页：手动指定"全局默认 Java"。</summary>
    private async void OnPickSettingsJavaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择默认 java.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Java 可执行文件") { Patterns = new[] { "java.exe", "javaw.exe" } },
                },
            });

            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) viewModel.SetSettingsCustomJava(path);
        }
        catch (Exception ex)
        {
            viewModel.ReportSettingsFailure($"选择 Java 失败：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>账号页：选择自定义皮肤 PNG（64×64）。</summary>
    private async void OnPickAccountSkinClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择皮肤 PNG（64×64）",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Minecraft 皮肤") { Patterns = new[] { "*.png" } },
                },
            });

            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) viewModel.SetAccountSkinFile(path);
        }
        catch (Exception ex)
        {
            viewModel.ReportSettingsFailure($"选择皮肤失败：{ex.GetBaseException().Message}");
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

    // ---- 模组 / 资源包 / 光影 / 数据包：把文件拖进来即导入 ----

    private void OnContentDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnContentDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (!e.Data.Contains(DataFormats.Files)) return;

        var paths = e.Data.GetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        if (paths is { Count: > 0 }) await viewModel.ImportDroppedAsync(paths);

        e.Handled = true;
    }
}
