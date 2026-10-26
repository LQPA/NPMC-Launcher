using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Storage;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class InstanceIdTests
{
    [Fact]
    public void Slug_keeps_chinese_and_turns_spaces_into_dashes()
    {
        Assert.Equal("原版生存", InstanceId.Slug("原版生存"));
        Assert.Equal("原版-生存", InstanceId.Slug("原版 生存"));
    }

    [Fact]
    public void Slug_replaces_filesystem_illegal_characters()
    {
        var slug = InstanceId.Slug("a<b>c:d\"e/f\\g");
        foreach (var illegal in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(illegal, slug);
    }

    [Fact]
    public void Slug_falls_back_when_name_is_blank()
    {
        Assert.Equal("instance", InstanceId.Slug(""));
        Assert.Equal("instance", InstanceId.Slug("   "));
    }

    [Fact]
    public void Slug_trims_leading_dots_and_trailing_dashes()
        => Assert.Equal("abc", InstanceId.Slug("...abc---"));

    [Fact]
    public void MakeUnique_appends_next_free_suffix()
        => Assert.Equal("原版-3", InstanceId.MakeUnique("原版", new[] { "原版", "原版-2" }));

    [Fact]
    public void MakeUnique_is_case_insensitive()
        => Assert.Equal("Vanilla-2", InstanceId.MakeUnique("Vanilla", new[] { "vanilla" }));

    [Fact]
    public void IsValid_rejects_separators_and_odd_names()
    {
        Assert.False(InstanceId.IsValid("a/b"));
        Assert.False(InstanceId.IsValid("a\\b"));
        Assert.False(InstanceId.IsValid(".."));
        Assert.False(InstanceId.IsValid(""));
        Assert.False(InstanceId.IsValid("ends with dot."));
        Assert.True(InstanceId.IsValid("原版生存"));
        Assert.True(InstanceId.IsValid("vanilla-1.20.1"));
    }
}

public class InstanceLayoutTests
{
    private static readonly LauncherPaths Launcher = new(Path.Combine(Path.GetTempPath(), "npmc-layout"));

    private static LauncherInstance Make(IsolationLevel isolation) =>
        LauncherInstance.Create("test-id", "测试实例", "1.20.1", isolation);

    [Fact]
    public void Default_isolation_is_version()
        => Assert.Equal(IsolationLevel.Version, LauncherInstance.Create("x", "x", "1.20.1").Isolation);

    [Fact]
    public void None_shares_the_launcher_root_as_game_directory()
    {
        var layout = new InstanceLayout(Launcher, Make(IsolationLevel.None));

        Assert.Equal(Launcher.Root, layout.GameDirectory);
        Assert.Equal(Launcher.Root, layout.SharedRoot);
        Assert.Equal(Path.Combine(Launcher.Root, "mods"), layout.Mods);

        // 无隔离时游戏目录就是根目录 —— 绝不能允许删除
        Assert.False(layout.OwnsGameDirectory);
    }

    [Fact]
    public void Version_isolates_game_directory_but_shares_libraries()
    {
        var layout = new InstanceLayout(Launcher, Make(IsolationLevel.Version));
        var expected = Path.Combine(Launcher.Instances, "test-id");

        Assert.Equal(expected, layout.GameDirectory);
        Assert.Equal(Launcher.Root, layout.SharedRoot);
        Assert.True(layout.OwnsGameDirectory);

        Assert.Equal(Path.Combine(expected, "mods"), layout.Mods);
        Assert.Equal(Path.Combine(expected, "resourcepacks"), layout.ResourcePacks);
        Assert.Equal(Path.Combine(expected, "shaderpacks"), layout.ShaderPacks);
        Assert.Equal(Path.Combine(expected, "saves"), layout.Saves);
        Assert.Equal(Path.Combine(expected, "config"), layout.Config);

        // versions / libraries / assets 仍在启动器根目录
        Assert.Equal(Path.Combine(Launcher.Root, "libraries"), layout.Paths.Libraries);
        Assert.Equal(Path.Combine(Launcher.Root, "versions"), layout.Paths.Versions);
        Assert.Equal(Path.Combine(Launcher.Root, "assets"), layout.Paths.Assets);

        // runtime 永远共享
        Assert.Equal(Path.Combine(Launcher.Root, "runtime"), layout.Paths.Runtime);
    }

    [Fact]
    public void Full_isolation_moves_libraries_and_assets_into_the_instance()
    {
        var layout = new InstanceLayout(Launcher, Make(IsolationLevel.Full));
        var expected = Path.Combine(Launcher.Instances, "test-id");

        Assert.Equal(expected, layout.GameDirectory);
        Assert.Equal(expected, layout.SharedRoot);
        Assert.Equal(Path.Combine(expected, "libraries"), layout.Paths.Libraries);
        Assert.Equal(Path.Combine(expected, "versions"), layout.Paths.Versions);
        Assert.Equal(Path.Combine(expected, "assets"), layout.Paths.Assets);

        // 但 runtime 仍然共享，避免每个实例重复装 Java
        Assert.Equal(Path.Combine(Launcher.Root, "runtime"), layout.Paths.Runtime);
    }

    [Fact]
    public void EnsureDirectories_creates_the_stable_subdirectories()
    {
        var root = NewTempDir();
        try
        {
            var layout = new InstanceLayout(new LauncherPaths(root), Make(IsolationLevel.Version));
            layout.EnsureDirectories();

            Assert.True(Directory.Exists(layout.GameDirectory));
            foreach (var directory in layout.StableDirectories)
                Assert.True(Directory.Exists(directory), directory);
        }
        finally { TryDelete(root); }
    }

    private static string NewTempDir() => TestTemp.NewDirectory("layout");

    private static void TryDelete(string dir) => TestTemp.TryDelete(dir);
}

public class SharedRootTests
{
    [Fact]
    public void SharedRoot_overrides_only_the_shared_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "npmc-sr-root");
        var shared = Path.Combine(root, "instances", "x");
        var paths = new LauncherPaths(root, shared);

        Assert.Equal(Path.Combine(shared, "versions"), paths.Versions);
        Assert.Equal(Path.Combine(shared, "libraries"), paths.Libraries);
        Assert.Equal(Path.Combine(shared, "assets"), paths.Assets);

        Assert.Equal(Path.Combine(root, "runtime"), paths.Runtime);
        Assert.Equal(Path.Combine(root, "instances"), paths.Instances);
    }

    [Fact]
    public void SharedRoot_defaults_to_root()
    {
        var paths = new LauncherPaths(@"C:\npmc");
        Assert.Equal(paths.Root, paths.SharedRoot);
    }
}

public class InstanceCatalogTests
{
    [Fact]
    public void Add_uses_version_isolation_by_default()
    {
        var catalog = new InstanceCatalog();
        var instance = catalog.Add("原版生存", "1.20.1");

        Assert.Equal("原版生存", instance.Id);
        Assert.Equal(IsolationLevel.Version, instance.Isolation);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Add_deduplicates_ids()
    {
        var catalog = new InstanceCatalog();
        catalog.Add("原版", "1.20.1");
        var second = catalog.Add("原版", "1.20.1");

        Assert.Equal("原版-2", second.Id);
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void Add_rejects_blank_name_or_version()
    {
        var catalog = new InstanceCatalog();
        Assert.Throws<ArgumentException>(() => catalog.Add("   ", "1.20.1"));
        Assert.Throws<ArgumentException>(() => catalog.Add("x", "  "));
    }

    [Fact]
    public void Remove_returns_false_for_unknown_id()
        => Assert.False(new InstanceCatalog().Remove("nope"));

    [Fact]
    public void Rename_changes_name_but_keeps_id()
    {
        var catalog = new InstanceCatalog();
        var created = catalog.Add("旧名", "1.20.1");

        var renamed = catalog.Rename(created.Id, "新名");

        Assert.NotNull(renamed);
        Assert.Equal(created.Id, renamed!.Id);
        Assert.Equal("新名", renamed.Name);
    }

    [Fact]
    public void SetIsolation_and_UpdateSettings_take_effect()
    {
        var catalog = new InstanceCatalog();
        var created = catalog.Add("x", "1.20.1");

        catalog.SetIsolation(created.Id, IsolationLevel.Full);
        catalog.UpdateSettings(created.Id, new InstanceSettings { MemoryMb = 8192 });

        var found = catalog.Find(created.Id)!;
        Assert.Equal(IsolationLevel.Full, found.Isolation);
        Assert.Equal(8192, found.Settings.MemoryMb);
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        var catalog = new InstanceCatalog();
        catalog.Add("Vanilla", "1.20.1");
        Assert.NotNull(catalog.Find("vanilla"));
    }

    [Fact]
    public void Instances_are_sorted_by_name()
    {
        var catalog = new InstanceCatalog();
        catalog.Add("b", "1.20.1");
        catalog.Add("a", "1.20.1");
        Assert.Equal("a", catalog.Instances[0].Name);
    }

    [Fact]
    public void Reorder_keeps_given_order_and_writes_sort_index()
    {
        var catalog = new InstanceCatalog(new[]
        {
            LauncherInstance.Create("a", "Alpha", "1.20.1"),
            LauncherInstance.Create("b", "Bravo", "1.20.1"),
            LauncherInstance.Create("c", "Charlie", "1.20.1"),
        });

        catalog.Reorder(new[] { "c", "a" });

        // 没列到的 b 保留在最后，避免调用方漏传导致实例"消失"
        Assert.Equal(new[] { "c", "a", "b" }, catalog.Instances.Select(i => i.Id));
        Assert.Equal(new[] { 0, 1, 2 }, catalog.Instances.Select(i => i.SortIndex));
    }

    [Fact]
    public void Relocate_changes_name_and_id_together()
    {
        var catalog = new InstanceCatalog(new[]
        {
            LauncherInstance.Create("old-id", "旧名字", "1.20.1"),
            LauncherInstance.Create("other", "别的", "1.20.1"),
        });

        var moved = catalog.Relocate("old-id", "新名字", "new-id");

        Assert.NotNull(moved);
        Assert.Equal("new-id", moved!.Id);
        Assert.Equal("新名字", moved.Name);
        Assert.Equal("new-id", catalog.Instances.First(i => i.Name == "新名字").Id);
    }

    [Fact]
    public void Relocate_rejects_an_id_that_another_instance_uses()
    {
        var catalog = new InstanceCatalog(new[]
        {
            LauncherInstance.Create("a", "A", "1.20.1"),
            LauncherInstance.Create("b", "B", "1.20.1"),
        });

        Assert.Throws<InvalidOperationException>(() => catalog.Relocate("a", "A2", "b"));
    }

    [Fact]
    public void Add_after_reorder_appends_to_the_end()
    {
        var catalog = new InstanceCatalog(new[]
        {
            LauncherInstance.Create("a", "Alpha", "1.20.1"),
            LauncherInstance.Create("b", "Bravo", "1.20.1"),
        });

        catalog.Reorder(new[] { "b", "a" });
        catalog.Add("Charlie", "1.20.1");

        Assert.Equal("Charlie", catalog.Instances[^1].Name);
    }

    [Fact]
    public void Validate_detects_duplicate_and_illegal_ids()
    {
        var catalog = new InstanceCatalog(new[]
        {
            LauncherInstance.Create("a", "x", "1.20.1"),
            LauncherInstance.Create("a", "y", "1.20.1"),
            LauncherInstance.Create("bad/id", "z", "1.20.1"),
        });

        var problems = catalog.Validate();

        Assert.Contains(problems, p => p.Contains("重复"));
        Assert.Contains(problems, p => p.Contains("非法"));
    }

    [Fact]
    public void Validate_is_empty_for_healthy_catalog()
    {
        var catalog = new InstanceCatalog();
        catalog.Add("a", "1.20.1");
        catalog.Add("b", "1.21");
        Assert.Empty(catalog.Validate());
    }
}

public class JsonInstanceStoreTests
{
    [Fact]
    public async Task Missing_file_yields_empty_list()
    {
        var dir = NewTempDir();
        try
        {
            var store = new JsonInstanceStore(new LauncherPaths(dir));
            Assert.Empty(await store.LoadAsync());
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Round_trip_preserves_fields_and_writes_enum_as_string()
    {
        var dir = NewTempDir();
        try
        {
            var store = new JsonInstanceStore(new LauncherPaths(dir));
            var instance = LauncherInstance.Create(
                "原版生存", "原版生存", "1.20.1", IsolationLevel.Full,
                new InstanceSettings { MemoryMb = 8192, WindowWidth = 1600, WindowHeight = 900 });

            await store.SaveAsync(new[] { instance });
            var loaded = await store.LoadAsync();

            Assert.Single(loaded);
            Assert.Equal("原版生存", loaded[0].Id);
            Assert.Equal(IsolationLevel.Full, loaded[0].Isolation);
            Assert.Equal(8192, loaded[0].Settings.MemoryMb);
            Assert.Equal(1600, loaded[0].Settings.WindowWidth);

            // 枚举写成字符串、中文不转义 —— 都是为了能直接手改配置
            var json = await File.ReadAllTextAsync(store.FilePath);
            Assert.Contains("\"Full\"", json);
            Assert.Contains("原版生存", json);
            Assert.DoesNotContain("\\u539F", json);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Save_leaves_no_tmp_file_behind()
    {
        var dir = NewTempDir();
        try
        {
            var store = new JsonInstanceStore(new LauncherPaths(dir));
            await store.SaveAsync(new[] { LauncherInstance.Create("a", "a", "1.20.1") });

            Assert.True(File.Exists(store.FilePath));
            Assert.False(File.Exists(store.FilePath + ".tmp"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Save_overwrites_previous_content()
    {
        var dir = NewTempDir();
        try
        {
            var store = new JsonInstanceStore(new LauncherPaths(dir));
            await store.SaveAsync(new[] { LauncherInstance.Create("a", "a", "1.20.1") });
            await store.SaveAsync(new[]
            {
                LauncherInstance.Create("b", "b", "1.20.1"),
                LauncherInstance.Create("c", "c", "1.20.1"),
            });

            var loaded = await store.LoadAsync();
            Assert.Equal(2, loaded.Count);
        }
        finally { TryDelete(dir); }
    }

    private static string NewTempDir() => TestTemp.NewDirectory("store");

    private static void TryDelete(string dir) => TestTemp.TryDelete(dir);
}
