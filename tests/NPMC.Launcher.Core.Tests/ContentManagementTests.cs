using NPMC.Launcher.Core.Content;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 内容扫描与启用/禁用/删除。
/// 全部在测试程序集目录下的 test-scratch 中操作，不依赖 %TEMP%。
/// </summary>
public class ContentScannerTests
{
    [Fact]
    public void Detects_both_enabled_and_disabled_mods()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            File.WriteAllText(Path.Combine(directory, "sodium.jar"), "");
            File.WriteAllText(Path.Combine(directory, "iris.jar.disabled"), "");

            var entries = ContentScanner.Scan(directory, ContentKind.Mod);

            Assert.Equal(2, entries.Count);

            var sodium = Assert.Single(entries, entry => entry.Name == "sodium");
            Assert.True(sodium.IsEnabled);
            Assert.Equal("sodium.jar", sodium.FileName);

            var iris = Assert.Single(entries, entry => entry.Name == "iris");
            Assert.False(iris.IsEnabled);
            Assert.Equal("iris.jar.disabled", iris.FileName);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Ignores_text_files_in_mods_directory()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "");
            File.WriteAllText(Path.Combine(directory, "notes.jar.txt"), "");
            File.WriteAllText(Path.Combine(directory, "real.jar"), "");

            var entries = ContentScanner.Scan(directory, ContentKind.Mod);

            Assert.Single(entries);
            Assert.Equal("real", entries[0].Name);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Skips_dot_entries_such_as_the_trash_directory()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".trash"));
            File.WriteAllText(Path.Combine(directory, "pack.zip"), "");

            var entries = ContentScanner.Scan(directory, ContentKind.ResourcePack);

            Assert.Single(entries);
            Assert.Equal("pack", entries[0].Name);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Resource_packs_accept_both_zip_and_folders()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            File.WriteAllText(Path.Combine(directory, "zipped.zip"), "");
            Directory.CreateDirectory(Path.Combine(directory, "foldered"));
            File.WriteAllText(Path.Combine(directory, "ignored.jar"), "");

            var entries = ContentScanner.Scan(directory, ContentKind.ResourcePack);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry is { Name: "zipped", IsDirectory: false });
            Assert.Contains(entries, entry => entry is { Name: "foldered", IsDirectory: true });
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Empty_or_missing_directory_yields_nothing()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            Assert.Empty(ContentScanner.Scan(directory, ContentKind.Mod));
            Assert.Empty(ContentScanner.Scan(Path.Combine(directory, "does-not-exist"), ContentKind.Mod));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }
}

public class ContentManagerTests
{
    [Fact]
    public void Toggle_round_trips_by_renaming()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            var path = Path.Combine(directory, "sodium.jar");
            File.WriteAllText(path, "payload");
            var manager = new ContentManager();

            var entry = ContentScanner.Scan(directory, ContentKind.Mod).Single();
            var disabled = manager.Toggle(entry, directory);

            Assert.False(disabled.IsEnabled);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ContentScanner.DisabledSuffix));

            var enabled = manager.Toggle(disabled, directory);

            Assert.True(enabled.IsEnabled);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ContentScanner.DisabledSuffix));
            Assert.Equal("payload", File.ReadAllText(path));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Toggle_refuses_when_the_target_name_is_taken()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            File.WriteAllText(Path.Combine(directory, "sodium.jar"), "");
            File.WriteAllText(Path.Combine(directory, "sodium.jar.disabled"), "");

            var entry = ContentScanner.Scan(directory, ContentKind.Mod).Single(item => item.IsEnabled);

            Assert.Throws<InvalidOperationException>(() => new ContentManager().Toggle(entry, directory));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Delete_moves_to_trash_by_default_so_it_stays_recoverable()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            var path = Path.Combine(directory, "sodium.jar");
            File.WriteAllText(path, "payload");

            var entry = ContentScanner.Scan(directory, ContentKind.Mod).Single();
            var movedTo = new ContentManager().Delete(entry, directory);

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(movedTo));
            Assert.Contains("sodium.jar", Path.GetFileName(movedTo));
            Assert.Equal(ContentScanner.TrashDirectoryName, Path.GetFileName(Path.GetDirectoryName(movedTo)));
            Assert.Equal("payload", File.ReadAllText(movedTo));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Permanent_delete_removes_the_file_entirely()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            var path = Path.Combine(directory, "sodium.jar");
            File.WriteAllText(path, "payload");

            var entry = ContentScanner.Scan(directory, ContentKind.Mod).Single();
            new ContentManager().Delete(entry, directory, permanent: true);

            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.Combine(directory, ContentScanner.TrashDirectoryName)));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Delete_works_on_folder_based_packs()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            var packDirectory = Path.Combine(directory, "MyPack");
            Directory.CreateDirectory(packDirectory);
            File.WriteAllText(Path.Combine(packDirectory, "pack.mcmeta"), "{}");

            var entry = ContentScanner.Scan(directory, ContentKind.ResourcePack).Single();
            new ContentManager().Delete(entry, directory);

            Assert.False(Directory.Exists(packDirectory));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public void Refuses_to_touch_a_path_outside_the_content_directory()
    {
        var directory = TestTemp.NewDirectory("content");
        var outside = TestTemp.NewDirectory("outside");
        try
        {
            var victim = Path.Combine(outside, "important.jar");
            File.WriteAllText(victim, "do not touch");

            var entry = new ContentEntry
            {
                Path = victim,
                Name = "important",
                FileName = "important.jar",
                Kind = ContentKind.Mod,
                IsEnabled = true,
                IsDirectory = false,
            };

            var manager = new ContentManager();

            Assert.Throws<InvalidOperationException>(() => manager.Toggle(entry, directory));
            Assert.Throws<InvalidOperationException>(() => manager.Delete(entry, directory));
            Assert.True(File.Exists(victim));
        }
        finally
        {
            TestTemp.TryDelete(directory);
            TestTemp.TryDelete(outside);
        }
    }

    [Fact]
    public void ToggleAll_switches_every_entry_of_one_kind()
    {
        var directory = TestTemp.NewDirectory("content");
        try
        {
            File.WriteAllText(Path.Combine(directory, "a.jar"), "");
            File.WriteAllText(Path.Combine(directory, "b.jar"), "");
            File.WriteAllText(Path.Combine(directory, "c.jar.disabled"), "");

            var manager = new ContentManager();
            Assert.Equal(2, manager.ToggleAll(directory, ContentKind.Mod, enable: false));

            Assert.All(ContentScanner.Scan(directory, ContentKind.Mod), entry => Assert.False(entry.IsEnabled));

            Assert.Equal(3, manager.ToggleAll(directory, ContentKind.Mod, enable: true));

            Assert.All(ContentScanner.Scan(directory, ContentKind.Mod), entry => Assert.True(entry.IsEnabled));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }
}
