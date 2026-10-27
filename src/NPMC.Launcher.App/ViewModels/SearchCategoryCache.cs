using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Core.Mods;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>一类项目（资源包 / 光影）的分类标签缓存。</summary>
internal sealed record CachedCategorySet(DateTimeOffset FetchedAt, List<ModrinthCategoryTag> Items);

/// <summary>
/// 「project_type → 分类标签」的磁盘缓存（`cache/search-categories.json`）。
///
/// 为什么要缓存：Modrinth 的分类基本不变，没必要每次启动都请求一次；
/// 断网时也能照常按分类筛选，而不是只剩一个"全部"。
/// 模组那 14 个中文分类不走这里（它们是写死的、带 CurseForge id）。
/// </summary>
internal sealed class SearchCategoryCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _file;
    private readonly Dictionary<string, CachedCategorySet> _sets = new(StringComparer.OrdinalIgnoreCase);

    public SearchCategoryCache(string cacheDirectory)
    {
        _file = Path.Combine(cacheDirectory, "search-categories.json");
        Load();
    }

    /// <summary>取缓存；不存在或超过 7 天返回 null。</summary>
    public IReadOnlyList<ModrinthCategoryTag>? Get(string projectType)
    {
        if (!_sets.TryGetValue(projectType, out var set)) return null;
        if (DateTimeOffset.UtcNow - set.FetchedAt > MaxAge) return null;
        return set.Items.Count == 0 ? null : set.Items;
    }

    public void Set(string projectType, IReadOnlyList<ModrinthCategoryTag> items)
    {
        _sets[projectType] = new CachedCategorySet(DateTimeOffset.UtcNow, items.ToList());
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_sets, Options));
        }
        catch
        {
            // 记不住不影响功能，下次重新拉
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;

            var map = JsonSerializer.Deserialize<Dictionary<string, CachedCategorySet>>(
                File.ReadAllText(_file), Options);
            if (map is null) return;

            foreach (var (key, value) in map) _sets[key] = value;
        }
        catch
        {
            // 文件损坏就当空
        }
    }
}
