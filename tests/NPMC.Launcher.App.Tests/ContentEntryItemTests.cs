using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// ViewModel 层的行为测试：这些类不依赖真实窗口，但容易在重构里悄悄坏掉。
/// </summary>
public class ContentEntryItemTests
{
    private static ContentEntry Entry(bool enabled = true) => new()
    {
        Path = @"C:\mods\example.jar",
        Name = "example",
        FileName = "example.jar",
        Kind = ContentKind.Mod,
        IsEnabled = enabled,
        IsDirectory = false,
        SizeBytes = 1024,
    };

    private static ContentEntryItem Create(ContentEntry entry) => new(
        entry,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    private static UpdateCandidate Candidate(string version, string fileName) => new()
    {
        Source = UpdateSource.Modrinth,
        ProjectId = "abc",
        VersionLabel = version,
        PublishedAt = DateTimeOffset.UtcNow,
        FileName = fileName,
        DownloadUrl = "https://example.invalid/mod.jar",
    };

    [Fact]
    public void SetUpdate_toggles_badge_and_text()
    {
        var item = Create(Entry());

        item.SetUpdate(new ModUpdate(
            item.Entry.Path, item.Entry.FileName,
            Candidate("1.0", "old.jar"), Candidate("2.0", "new.jar")));

        Assert.True(item.HasUpdate);
        Assert.Equal("1.0 → 2.0", item.UpdateText);

        item.SetUpdate(null);

        Assert.False(item.HasUpdate);
        Assert.Equal("", item.UpdateText);
    }

    [Fact]
    public void ApplyEntry_updates_enabled_state_and_toggle_text()
    {
        var item = Create(Entry());
        Assert.True(item.IsEnabled);
        Assert.Equal("禁用", item.ToggleText);
        Assert.Equal("已启用", item.StatusText);

        item.ApplyEntry(item.Entry with
        {
            Path = @"C:\mods\example.jar.disabled",
            FileName = "example.jar.disabled",
            IsEnabled = false,
        });

        Assert.False(item.IsEnabled);
        Assert.Equal("启用", item.ToggleText);
        Assert.Equal("已禁用", item.StatusText);
    }

    [Fact]
    public void Selection_chip_raises_changed_only_on_actual_change()
    {
        var chip = new SearchCategoryChip("technology", "科技", "机器与自动化");
        var changes = 0;
        chip.Changed += _ => changes++;

        chip.IsSelected = true;
        chip.IsSelected = true;
        chip.IsSelected = false;

        Assert.False(chip.IsSelected);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Known_category_has_chinese_name_and_description()
    {
        var (display, description) = ModCategoryInfo.Describe("technology");

        Assert.Equal("科技", display);
        Assert.Contains("自动化", description);
    }

    [Fact]
    public void Unknown_category_falls_back_to_its_slug()
    {
        var (display, description) = ModCategoryInfo.Describe("some-new-category");

        Assert.Equal("some-new-category", display);
        Assert.Contains("Modrinth", description);
    }
}
