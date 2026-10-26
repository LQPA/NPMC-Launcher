using System.Text.Json;
using NPMC.Launcher.Infrastructure.Minecraft;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>某个游戏版本已查到的加载器清单，带检查时间。</summary>
public sealed record LoaderAvailabilitySnapshot(
    string MinecraftVersion,
    DateTimeOffset CheckedAt,
    List<FabricLoaderVersion> Fabric,
    List<ForgeLoaderVersion> Forge,
    List<ForgeLoaderVersion> NeoForge);

/// <summary>
/// 加载器清单的磁盘缓存（&lt;root&gt;\cache\loader-versions.json）。
///
/// 为什么要落盘：切回一个已经看过的版本时，Fabric launcher meta、Forge / NeoForge
/// 的 maven-metadata 都不会变，再查一遍纯属浪费（还会让界面卡在"正在检查加载器…"）。
/// 内存缓存只活一次进程，落盘后重开启动器也不用重查。
///
/// 过期时间 7 天：加载器更新不频繁，但也不能永远不刷新 —— 界面上有「重新检查」按钮强制刷新。
/// </summary>
public sealed class LoaderAvailabilityCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Dictionary<string, LoaderAvailabilitySnapshot> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public LoaderAvailabilityCache(string launcherRoot)
    {
        _path = Path.Combine(launcherRoot, "cache", "loader-versions.json");
        Load();
    }

    public string FilePath => _path;

    public bool TryGet(string minecraftVersion, out LoaderAvailabilitySnapshot snapshot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(minecraftVersion, out snapshot!)
                && DateTimeOffset.UtcNow - snapshot.CheckedAt <= MaxAge)
            {
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public void Store(LoaderAvailabilitySnapshot snapshot)
    {
        lock (_gate)
        {
            _entries[snapshot.MinecraftVersion] = snapshot;
            Save();
        }
    }

    public void Remove(string minecraftVersion)
    {
        lock (_gate)
        {
            if (_entries.Remove(minecraftVersion)) Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, LoaderAvailabilitySnapshot>>(json, Options);
            if (entries is null) return;

            foreach (var (key, value) in entries) _entries[key] = value;
        }
        catch
        {
            // 缓存坏了就当没有；下次检查会重写
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Options));
        }
        catch
        {
            // 写不了缓存不影响功能
        }
    }
}
