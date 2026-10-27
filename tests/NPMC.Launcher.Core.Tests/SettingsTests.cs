using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Storage;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class LauncherSettingsTests
{
    [Fact]
    public void Defaults_are_official_source_with_fallback()
    {
        var settings = new LauncherSettings();

        Assert.Equal(DownloadSource.Official, settings.PreferredSource);
        Assert.True(settings.AllowSourceFallback);
        Assert.Equal(4096, settings.DefaultMemoryMb);
        Assert.True(settings.DownloadConcurrency > 0);
    }

    [Fact]
    public void ToMirrorSettings_copies_every_relevant_field()
    {
        var settings = new LauncherSettings
        {
            PreferredSource = DownloadSource.Bmclapi,
            BmclapiBaseUrl = "https://mirror.example.com",
            AllowSourceFallback = false,
        };

        var mirror = settings.ToMirrorSettings();

        Assert.Equal(DownloadSource.Bmclapi, mirror.Source);
        Assert.Equal("https://mirror.example.com", mirror.BmclapiBaseUrl);
        Assert.False(mirror.AllowFallback);
    }

    [Fact]
    public void ToMirrorSettings_result_is_a_record_so_source_can_be_overridden()
    {
        var settings = new LauncherSettings();
        var overridden = settings.ToMirrorSettings() with { Source = DownloadSource.Bmclapi };

        Assert.Equal(DownloadSource.Bmclapi, overridden.Source);
        Assert.Equal(settings.BmclapiBaseUrl, overridden.BmclapiBaseUrl);
    }
}

public class JsonSettingsStoreTests
{
    [Fact]
    public async Task Missing_file_returns_defaults()
    {
        var directory = TestTemp.NewDirectory("settings");
        try
        {
            var store = new JsonSettingsStore(new LauncherPaths(directory));
            var settings = await store.LoadAsync();

            Assert.Equal(DownloadSource.Official, settings.PreferredSource);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public async Task Round_trip_preserves_values_and_writes_enum_as_string()
    {
        var directory = TestTemp.NewDirectory("settings");
        try
        {
            var store = new JsonSettingsStore(new LauncherPaths(directory));
            await store.SaveAsync(new LauncherSettings
            {
                PreferredSource = DownloadSource.Bmclapi,
                DefaultMemoryMb = 8192,
                DownloadConcurrency = 20,
                AllowSourceFallback = false,
                IncludePrereleaseUpdates = true,
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(DownloadSource.Bmclapi, loaded.PreferredSource);
            Assert.Equal(8192, loaded.DefaultMemoryMb);
            Assert.Equal(20, loaded.DownloadConcurrency);
            Assert.False(loaded.AllowSourceFallback);
            Assert.True(loaded.IncludePrereleaseUpdates);

            var json = await File.ReadAllTextAsync(store.FilePath);
            Assert.Contains("\"Bmclapi\"", json);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public async Task Save_overwrites_previous_settings()
    {
        var directory = TestTemp.NewDirectory("settings");
        try
        {
            var store = new JsonSettingsStore(new LauncherPaths(directory));

            await store.SaveAsync(new LauncherSettings { DefaultMemoryMb = 2048 });
            await store.SaveAsync(new LauncherSettings { DefaultMemoryMb = 6144 });

            Assert.Equal(6144, (await store.LoadAsync()).DefaultMemoryMb);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }
}
