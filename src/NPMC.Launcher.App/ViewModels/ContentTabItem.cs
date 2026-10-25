using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>右侧页签的种类。</summary>
public enum DetailTabKind
{
    /// <summary>实例概览。</summary>
    Overview = 0,

    /// <summary>某类内容的列表（模组 / 资源包 / 光影）。</summary>
    Content = 1,

    /// <summary>游戏日志。</summary>
    Log = 2,
}

/// <summary>右侧一个页签。</summary>
public sealed class ContentTabItem : ObservableObject
{
    private int _count;
    private bool _isEnabled = true;
    private string _disabledHint = "";

    public ContentTabItem(string title, DetailTabKind kind, ContentKind? contentKind = null)
    {
        Title = title;
        Kind = kind;
        ContentKind = contentKind;
    }

    public string Title { get; }

    public DetailTabKind Kind { get; }

    /// <summary>仅当 <see cref="Kind"/> 为 Content 时有意义。</summary>
    public ContentKind? ContentKind { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    /// <summary>
    /// 该页签是否可用。
    ///
    /// 模组需要实例装了模组加载器，光影还需要 Iris/OptiFine —— 否则文件放到目录里
    /// 游戏也不会加载，给一个能点进去的空页签等于误导。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(TabTip));
        }
    }

    /// <summary>不可用时的原因（只在不可用时作为提示显示）。</summary>
    public string DisabledHint
    {
        get => _disabledHint;
        private set
        {
            if (SetProperty(ref _disabledHint, value)) OnPropertyChanged(nameof(TabTip));
        }
    }

    /// <summary>可用时返回 null，避免弹出一个空提示框。</summary>
    public string? TabTip => _isEnabled ? null : _disabledHint;

    public void SetAvailability(bool enabled, string hint)
    {
        IsEnabled = enabled;
        DisabledHint = enabled ? "" : hint;
    }

    /// <summary>只有内容页签显示数量，概览与日志不显示。</summary>
    public string DisplayTitle => Kind == DetailTabKind.Content && Count > 0 ? $"{Title} {Count}" : Title;
}
