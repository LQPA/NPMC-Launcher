using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 实例列表的排序规则。
///
/// 收藏组固定置顶：这样「只显示收藏」时可见列表正好就是收藏组，
/// 拖动只在组内发生，不存在"和非收藏项比较插在哪"的问题（2026-11-08 与用户确认的方案）。
/// </summary>
public class InstanceOrderTests
{
    [Fact]
    public void Favorites_are_pinned_to_the_top_keeping_their_own_order()
    {
        var a = CreateItem("a");
        var b = CreateItem("b", favorite: true);
        var c = CreateItem("c");
        var d = CreateItem("d", favorite: true);

        var ordered = MainWindowViewModel.BuildDisplayOrder(new[] { a, b, c, d });

        // 收藏组（b、d）在前，且组内保持原有相对顺序；非收藏组（a、c）同理
        Assert.Equal(new[] { "b", "d", "a", "c" }, ordered.Select(item => item.Id));
    }

    [Fact]
    public void Only_same_group_items_can_be_reordered_against_each_other()
    {
        var favoriteA = CreateItem("fa", favorite: true);
        var favoriteB = CreateItem("fb", favorite: true);
        var normal = CreateItem("n");

        Assert.True(MainWindowViewModel.CanReorder(favoriteA, favoriteB));
        Assert.True(MainWindowViewModel.CanReorder(normal, CreateItem("n2")));
        Assert.False(MainWindowViewModel.CanReorder(favoriteA, normal));
        Assert.False(MainWindowViewModel.CanReorder(normal, favoriteA));
    }

    [Fact]
    public void Filtering_to_favorites_yields_exactly_the_pinned_prefix()
    {
        var a = CreateItem("a", favorite: true);
        var b = CreateItem("b");
        var c = CreateItem("c", favorite: true);

        var ordered = MainWindowViewModel.BuildDisplayOrder(new[] { a, b, c });
        var favoritesOnly = ordered.Where(item => item.IsFavorite).ToList();

        Assert.Equal(new[] { "a", "c" }, favoritesOnly.Select(item => item.Id));
        // 也就是显示顺序的前两项，拖动只会在这一段里发生
        Assert.Equal(ordered.Take(2).Select(item => item.Id), favoritesOnly.Select(item => item.Id));
    }

    [Fact]
    public void Favoriting_moves_the_item_to_the_very_top_of_the_favorites_group()
    {
        var f1 = CreateItem("f1", favorite: true);
        var f2 = CreateItem("f2", favorite: true);
        var n1 = CreateItem("n1");
        var n2 = CreateItem("n2");

        // n2 刚被收藏
        n2.IsFavorite = true;
        var ordered = MainWindowViewModel.PinToGroupTop(new[] { f1, f2, n1, n2 }, n2);

        Assert.Equal(new[] { "n2", "f1", "f2", "n1" }, ordered.Select(item => item.Id));
    }

    [Fact]
    public void Unfavoriting_moves_the_item_to_the_top_of_the_normal_group()
    {
        var f1 = CreateItem("f1", favorite: true);
        var f2 = CreateItem("f2", favorite: true);
        var n1 = CreateItem("n1");
        var n2 = CreateItem("n2");

        // f2 刚被取消收藏 → 收藏组下方第一行，其余非收藏项保持原相对顺序
        f2.IsFavorite = false;
        var ordered = MainWindowViewModel.PinToGroupTop(new[] { f1, f2, n1, n2 }, f2);

        Assert.Equal(new[] { "f1", "f2", "n1", "n2" }, ordered.Select(item => item.Id));
    }

    private static InstanceListItem CreateItem(string id, bool favorite = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-order-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var launcher = new LauncherPaths(root);
        return new InstanceListItem(
            launcher,
            new LauncherInstance { Id = id, Name = id, VersionId = "1.20.1" })
        {
            IsFavorite = favorite,
        };
    }
}
