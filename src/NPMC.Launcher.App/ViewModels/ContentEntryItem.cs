using System.Windows.Input;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Mods;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 内容列表里的一行。
///
/// 两条关键约束：
///   1. 启用/禁用之后**必须就地更新这一行**，不能重建集合 ——
///      重建会让列表丢失选中项，表现为"点禁用后高亮跳到别的行再跳回来"。
///   2. 升级提示（<see cref="PendingUpdate"/>）也是就地贴上去的，
///      检查更新不该引起列表重建。
///
/// 命令挂在条目自己身上而不是窗口 VM 上：
///   XAML 里不需要写 <c>$parent[Window].DataContext...</c> 这类跨层绑定，
///   编译期绑定检查也就能直接覆盖到它们。
/// </summary>
public sealed class ContentEntryItem : ObservableObject
{
    private ContentEntry _entry;
    private ModUpdate? _pendingUpdate;
    private UpdateSource? _matchedSource;
    private bool _identified;

    public ContentEntryItem(
        ContentEntry entry,
        Func<ContentEntryItem, Task> toggle,
        Func<ContentEntryItem, Task> delete,
        Func<ContentEntryItem, Task>? upgrade = null)
    {
        _entry = entry;

        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
        UpgradeCommand = new AsyncRelayCommand(() => upgrade?.Invoke(this) ?? Task.CompletedTask);
    }

    public ContentEntry Entry => _entry;

    public string Name => _entry.Name;

    public bool IsEnabled => _entry.IsEnabled;

    public string ToggleText => _entry.IsEnabled ? "禁用" : "启用";

    public string StatusText => _entry.IsEnabled ? "已启用" : "已禁用";

    /// <summary>
    /// 副标题。**刻意不含修改时间**：模组文件的写入时间对用户没有意义，
    /// 真正有用的是"文件名 + 体积"，日期只会挤占横向空间、增加噪音。
    /// </summary>
    public string MetaText => $"{_entry.FileName} · {FormatSize(_entry.SizeBytes)}";

    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UpgradeCommand { get; }

    // ---- 识别状态（检查更新后才会有结论）----

    /// <summary>是否已经做过识别。用于区分"还没检查"与"检查了但认不出"。</summary>
    public bool IsIdentified => _identified;

    /// <summary>检查过、但两个平台都认不出。</summary>
    public bool IsUnidentified => _identified && _matchedSource is null;

    public bool IsFromModrinth => _matchedSource == UpdateSource.Modrinth;

    public bool IsFromCurseForge => _matchedSource == UpdateSource.CurseForge;

    /// <summary>记录识别来源；传 null 表示已检查但没认出来。</summary>
    /// <param name="channel">识别出的版本通道；未识别时为 null。</param>
    public void SetIdentification(UpdateSource? source, ReleaseChannel? channel = null)
    {
        _matchedSource = source;
        _channel = source is null ? null : channel;
        _identified = true;

        OnPropertyChanged(nameof(IsIdentified));
        OnPropertyChanged(nameof(IsUnidentified));
        OnPropertyChanged(nameof(IsFromModrinth));
        OnPropertyChanged(nameof(IsFromCurseForge));
        OnPropertyChanged(nameof(HasChannel));
        OnPropertyChanged(nameof(ChannelBadge));
        OnPropertyChanged(nameof(ChannelLabel));
        OnPropertyChanged(nameof(ChannelColor));
    }

    // ---- 版本通道（R / B / A）----

    private ReleaseChannel? _channel;

    /// <summary>识别成功且知道版本身份时才显示通道徽标。</summary>
    public bool HasChannel => _channel is not null;

    public string ChannelBadge => _channel?.Badge() ?? "";

    public string ChannelLabel => _channel?.DisplayName() ?? "";

    /// <summary>与安装下拉里的版本徽标同色：绿 / 琥珀 / 红。</summary>
    public string ChannelColor => _channel switch
    {
        ReleaseChannel.Alpha => "#A93226",
        ReleaseChannel.Beta => "#94611A",
        _ => "#2E7D46",
    };

    // ---- 升级提示 ----

    /// <summary>检查更新后贴上的升级信息；为 null 表示没有可用更新。</summary>
    public ModUpdate? PendingUpdate => _pendingUpdate;

    public bool HasUpdate => _pendingUpdate is not null;

    public string UpdateText => _pendingUpdate is null
        ? ""
        : $"{_pendingUpdate.Current.VersionLabel} → {_pendingUpdate.Latest.VersionLabel}";

    public void SetUpdate(ModUpdate? update)
    {
        _pendingUpdate = update;

        OnPropertyChanged(nameof(PendingUpdate));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateText));
    }

    /// <summary>用切换/改名后的条目替换自身数据，只发属性通知，不动所在的集合。</summary>
    public void ApplyEntry(ContentEntry entry)
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
