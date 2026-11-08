using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Paths;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 收藏的持久化。
///
/// 单独一个 favorites.json：instances.json 是 CLI / 整合包导出共用的数据，
/// 不该为了一条界面偏好去动它的结构。
/// </summary>
public class InstanceFavoritesStoreTests
{
    [Fact]
    public void Favorites_roundTrip_through_the_file_and_drop_duplicates()
    {
        var root = CreateTempRoot();
        try
        {
            var launcher = new LauncherPaths(root);

            InstanceFavoritesStore.Save(launcher, new[] { "atm10", "fabulously-optimized", "atm10" });
            var loaded = InstanceFavoritesStore.Load(launcher);

            Assert.Equal(2, loaded.Count);
            Assert.Contains("atm10", loaded);
            Assert.Contains("fabulously-optimized", loaded);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Missing_file_means_no_favorites()
    {
        var root = CreateTempRoot();
        try
        {
            var launcher = new LauncherPaths(root);
            Assert.Empty(InstanceFavoritesStore.Load(launcher));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Broken_file_does_not_throw()
    {
        var root = CreateTempRoot();
        try
        {
            var launcher = new LauncherPaths(root);
            File.WriteAllText(Path.Combine(root, "favorites.json"), "{ 这不是 json");
            Assert.Empty(InstanceFavoritesStore.Load(launcher));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-favorites-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响测试结论
        }
    }
}
