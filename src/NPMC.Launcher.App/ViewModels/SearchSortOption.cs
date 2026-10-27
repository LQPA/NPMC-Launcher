namespace NPMC.Launcher.App.ViewModels;

/// <summary>搜索排序：Modrinth 用 index 名称，CurseForge 用 sortField。</summary>
public sealed record SearchSortOption(
    string Display,
    string ModrinthIndex,
    int CurseForgeSortField);
