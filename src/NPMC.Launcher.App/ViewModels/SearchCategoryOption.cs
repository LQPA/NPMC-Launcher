namespace NPMC.Launcher.App.ViewModels;

/// <summary>模组搜索的分类筛选：Modrinth 用 categories facet，CurseForge 用 categoryId。</summary>
public sealed record SearchCategoryOption(
    string Display,
    string? ModrinthCategory,
    int? CurseForgeCategoryId);
