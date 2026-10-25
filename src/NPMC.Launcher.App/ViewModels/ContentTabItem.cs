using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>内容页签（概览 / 模组 / 资源包 / 光影）。</summary>
public sealed class ContentTabItem : ObservableObject
{
    private int _count;

    public ContentTabItem(string title, ContentKind? kind)
    {
        Title = title;
        Kind = kind;
    }

    public string Title { get; }

    /// <summary>为 null 表示「概览」页。</summary>
    public ContentKind? Kind { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string DisplayTitle => Count > 0 ? $"{Title} {Count}" : Title;
}
