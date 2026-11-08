using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 实例列表搜索的匹配规则。
///
/// 这些都是 2026-11-08 真实踩到的 NullReferenceException 的回归用例：
///   - TextBox 首次绑定/清空时把 null 推给 InstanceSearchText（setter 里直接 .Trim() 就炸）；
///   - ModpackName 是 string?，非整合包实例为 null（.Contains 一样炸）。
/// 两处都曾被 Avalonia 的验证插件画成输入框上的红框异常提示。
/// </summary>
public class InstanceSearchTests
{
    [Fact]
    public void Matches_name_version_and_id()
    {
        var item = CreateItem("1.20.1-forge", "1.20.1 生存", "1.20.1-forge-47.2.0");

        Assert.True(MainWindowViewModel.MatchesInstance(item, "1.20.1"));
        Assert.True(MainWindowViewModel.MatchesInstance(item, "forge"));
        Assert.True(MainWindowViewModel.MatchesInstance(item, "生存"));
        Assert.False(MainWindowViewModel.MatchesInstance(item, "26.2"));
    }

    [Fact]
    public void Missing_modpack_name_does_not_throw()
    {
        var item = CreateItem("1.12.2", "1.12.2", "1.12.2");

        Assert.Null(item.ModpackName);
        Assert.True(MainWindowViewModel.MatchesInstance(item, "1.12.2"));
        Assert.False(MainWindowViewModel.MatchesInstance(item, "atm10"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var item = CreateItem("ATM10", "All the Mods 10", "1.20.1-forge-47.2.0");

        Assert.True(MainWindowViewModel.MatchesInstance(item, "atm"));
        Assert.True(MainWindowViewModel.MatchesInstance(item, "ALL THE MODS"));
    }

    private static InstanceListItem CreateItem(string id, string name, string versionId)
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var launcher = new LauncherPaths(root);
        return new InstanceListItem(
            launcher,
            new LauncherInstance { Id = id, Name = name, VersionId = versionId });
    }
}
