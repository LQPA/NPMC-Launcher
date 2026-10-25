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

    /// <summary>只有内容页签显示数量，概览与日志不显示。</summary>
    public string DisplayTitle => Kind == DetailTabKind.Content && Count > 0 ? $"{Title} {Count}" : Title;
}
