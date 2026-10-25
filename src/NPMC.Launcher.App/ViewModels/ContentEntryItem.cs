using System.Windows.Input;
using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 内容列表里的一行。
///
/// 命令挂在**条目自己**身上而不是窗口 VM 上：
///   XAML 里就不需要写 <c>$parent[Window].DataContext...</c> 这类跨层绑定，
///   编译期绑定检查也就能直接覆盖到它们。
/// 条目在每次刷新时重建，因此同样不需要属性通知。
/// </summary>
public sealed class ContentEntryItem
{
    public ContentEntryItem(
        ContentEntry entry,
        Func<ContentEntryItem, Task> toggle,
        Func<ContentEntryItem, Task> delete)
    {
        Entry = entry;
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public ContentEntry Entry { get; }

    public string Name => Entry.Name;

    public bool IsEnabled => Entry.IsEnabled;

    public string ToggleText => Entry.IsEnabled ? "禁用" : "启用";

    public string StatusText => Entry.IsEnabled ? "已启用" : "已禁用";

    public string MetaText =>
        $"{Entry.FileName} · {FormatSize(Entry.SizeBytes)} · {Entry.ModifiedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:N2} GB",
    };
}
