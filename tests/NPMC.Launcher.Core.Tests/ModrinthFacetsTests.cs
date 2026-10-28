using NPMC.Launcher.Infrastructure.Modrinth;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// Modrinth facet 组内 OR、组间 AND。多选分类必须是多个独立组（AND / 取交集），
/// 不能塞进同一个组（那会变成 OR）。
/// </summary>
public class ModrinthFacetsTests
{
    [Fact]
    public void Multiple_categories_become_separate_groups()
    {
        var facets = ModrinthClient.BuildFacets(
            "mod", "1.12.2", new[] { "forge" }, new[] { "technology", "storage" });

        Assert.Contains("[\"categories:technology\"]", facets);
        Assert.Contains("[\"categories:storage\"]", facets);

        // 同一个元素里同时出现两个分类 = 被合成了 OR 组，这里必须不出现
        Assert.DoesNotContain(facets, facet =>
            facet.Contains("technology", StringComparison.Ordinal)
            && facet.Contains("storage", StringComparison.Ordinal));

        Assert.Contains("[\"versions:1.12.2\"]", facets);
        Assert.Contains("[\"categories:forge\"]", facets);
    }

    [Fact]
    public void Single_category_stays_one_group()
    {
        var facets = ModrinthClient.BuildFacets(
            "mod", null, Array.Empty<string>(), new[] { "storage" });

        Assert.Contains("[\"categories:storage\"]", facets);
        Assert.DoesNotContain(facets, facet => facet.Contains("versions:", StringComparison.Ordinal));
    }

    [Fact]
    public void Secondary_category_is_its_own_group()
    {
        var facets = ModrinthClient.BuildFacets(
            "resourcepack", "1.20.1", Array.Empty<string>(),
            new[] { "16x" }, secondaryCategory: "vanilla-like");

        Assert.Contains("[\"categories:16x\"]", facets);
        Assert.Contains("[\"categories:vanilla-like\"]", facets);
    }
}
