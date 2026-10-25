using System.Windows.Input;
using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 内容列表里的一行。
///
/// 关键约束：启用/禁用之后**必须就地更新这一行**，不能重建集合。
/// 曾经的做法是 Content.Clear() 再全量重填，结果 ListBox 的选中项被清空，
/// 表现为"点禁用后高亮跳到别的行再跳回来"。
///
/// 命令挂在条目自己身上而不是窗口 VM 上：
///   XAML 里不需要写 <c>$parent[Window].DataContext...</c> 这类跨层绑定，
///   编译期绑定检查也就能直接覆盖到它们。
/// </summary>
public sealed class ContentEntryItem : ObservableObject
{
    private ContentEntry _entry;

    public ContentEntryItem(
        ContentEntry entry,
        Func<ContentEntryItem, Task> toggle,
        Func<ContentEntryItem, Task> delete)
    {
        _entry = entry;
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public ContentEntry Entry => _entry;

    public string Name => _entry.Name;

    public bool IsEnabled => _entry.IsEnabled;

    public string ToggleText => _entry.IsEnabled ? "禁用" : "启用";

    public string StatusText => _entry.IsEnabled ? "已启用" : "已禁用";

    public string MetaText =>
        $"{_entry.FileName} · {FormatSize(_entry.SizeBytes)} · {_entry.ModifiedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>用切换后的条目替换自身数据，只发属性通知，不动所在的集合。</summary>
    public void Update(ContentEntry entry)
    {
        _entry = entry;

        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MetaText));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:N2} GB",
    };
}
