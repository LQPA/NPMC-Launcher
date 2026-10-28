namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 搜索栏里的一个可多选的分类标签（Modrinth 维度）。
///
/// 和原来的下拉框不同：勾选多个分类时取**交集**（Modrinth 的 facet 规则：
/// 组内 OR、组间 AND），例如「科技 + 存储」只留下同时带这两个标签的项目。
/// </summary>
public sealed class SearchCategoryChip : ObservableObject
{
    public SearchCategoryChip(string slug, string display, string description)
    {
        Slug = slug;
        Display = display;
        Description = description;
    }

    /// <summary>Modrinth 分类 slug（facet 里用的值）。</summary>
    public string Slug { get; }

    public string Display { get; }

    /// <summary>悬停说明：这个分类到底包含什么、适合和哪些分类组合。</summary>
    public string Description { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            Changed?.Invoke(this);
        }
    }

    /// <summary>选中状态变化（ViewModel 用来切换来源/刷新条件提示）。</summary>
    public event Action<SearchCategoryChip>? Changed;
}
