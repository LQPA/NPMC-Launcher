using System.IO.Compression;
using System.Text.Json;

namespace NPMC.Launcher.App.Mods;

/// <summary>
/// jar → 包名索引：回答"这个类/包由哪个模组 jar 提供"。
///
/// 为什么需要单独的索引：诊断里要把 <c>NoClassDefFoundError: net/caffeinemc/...</c> 反查到模组，
/// 原来是一次性遍历每个 jar 的中央目录（400 个模组 ≈ 1–2 秒，且每次查询都重来）。
/// 这里按 jar 的 (路径, 大小, 修改时间) 缓存每个 jar 拥有的**包名集合**，
/// 之后一次查询是纯字典查找。
///
/// 只索引到包名而不索引到类名，是为了控制缓存体积：
/// 一个 400 模组的整合包类名可能有几十万条，包名通常只有几万条。
/// 反查时按"最长包前缀"匹配，足够定位到是哪个 jar。
/// </summary>
public sealed class ClassIndex
{
    private const int CacheVersion = 1;
    private const int MaxPackagesPerJar = 40000;

    private readonly Dictionary<string, string> _packageToJar = new(StringComparer.Ordinal);

    private ClassIndex()
    {
    }

    /// <summary>已建索引的 jar 数（诊断用）。</summary>
    public int JarCount { get; private set; }

    /// <summary>已索引的包名总数（诊断用）。</summary>
    public int PackageCount => _packageToJar.Count;

    public static ClassIndex Empty { get; } = new();

    /// <summary>
    /// 构建（或从缓存加载）索引。
    /// 缓存按 jar 粒度复用：(路径, 大小, 修改时间) 变了才重新扫这个 jar。
    /// </summary>
    public static ClassIndex Build(IEnumerable<string> jarPaths, string? cacheFilePath = null)
    {
        var jars = jarPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (jars.Count == 0)
        {
            return Empty;
        }

        var cached = LoadCache(cacheFilePath);
        var index = new ClassIndex();
        var freshEntries = new List<CacheEntry>();
        var changed = false;

        foreach (var jarPath in jars)
        {
            string key;
            long size;
            long mtime;
            try
            {
                var info = new FileInfo(jarPath);
                if (!info.Exists) continue;
                size = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
                key = $"{jarPath}|{size}|{mtime}";
            }
            catch
            {
                continue;
            }

            if (cached.TryGetValue(key, out var hit))
            {
                freshEntries.Add(hit);
                index.AddPackages(jarPath, hit.Packages);
                continue;
            }

            changed = true;
            var packages = ScanPackages(jarPath);
            var entry = new CacheEntry { Key = key, JarPath = jarPath, Packages = packages };
            freshEntries.Add(entry);
            index.AddPackages(jarPath, packages);
        }

        if (cacheFilePath is not null && (changed || !File.Exists(cacheFilePath)))
        {
            SaveCache(cacheFilePath, freshEntries);
        }

        return index;
    }

    /// <summary>
    /// 反查某个类最可能的提供者。返回匹配到的包前缀与 jar 路径。
    /// 只看包名，所以"这个类确实不在任何 jar 里"也能快速判定（返回 false）。
    /// </summary>
    public bool TryFindProvider(string className, out string jarPath, out string matchedPackage)
    {
        jarPath = string.Empty;
        matchedPackage = string.Empty;

        var package = ToPackage(className);
        if (package.Length == 0)
        {
            return false;
        }

        // 从最长包名开始逐级截短：net/a/b/c → net/a/b → net/a
        var candidate = package;
        while (candidate.Length > 0)
        {
            if (_packageToJar.TryGetValue(candidate, out var found))
            {
                jarPath = found;
                matchedPackage = candidate;
                return true;
            }

            var cut = candidate.LastIndexOf('/');
            if (cut <= 0)
            {
                break;
            }

            candidate = candidate[..cut];
        }

        return false;
    }

    /// <summary>类名 → 包名（内部名，斜杠分隔）。</summary>
    private static string ToPackage(string className)
    {
        var name = className.Trim().Replace('\\', '/').Replace('.', '/').Trim('/');
        if (name.Length == 0) return string.Empty;

        var cut = name.LastIndexOf('/');
        return cut <= 0 ? string.Empty : name[..cut];
    }

    private void AddPackages(string jarPath, IEnumerable<string> packages)
    {
        JarCount++;
        foreach (var package in packages)
        {
            // 先到先得：同一个包出现在多个 jar 里时，保留第一个（列表顺序确定，结果可复现）
            _packageToJar.TryAdd(package, jarPath);
        }
    }

    /// <summary>扫一个 jar 的条目，收集它声明过的包名（不读文件内容，只读中央目录）。</summary>
    private static List<string> ScanPackages(string jarPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!name.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) continue;

                var cut = name.LastIndexOf('/');
                if (cut <= 0) continue;

                result.Add(name[..cut]);
                if (result.Count >= MaxPackagesPerJar) break;
            }
        }
        catch
        {
            // jar 损坏/被占用：交给「jar 损坏」规则处理，这里不抛
        }

        return result.ToList();
    }

    private static Dictionary<string, CacheEntry> LoadCache(string? cacheFilePath)
    {
        var result = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(cacheFilePath) || !File.Exists(cacheFilePath))
        {
            return result;
        }

        try
        {
            var model = JsonSerializer.Deserialize<CacheModel>(File.ReadAllText(cacheFilePath));
            if (model is not { Version: CacheVersion })
            {
                return result;
            }

            foreach (var entry in model.Jars)
            {
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    result[entry.Key] = entry;
                }
            }
        }
        catch
        {
            // 缓存坏了重建即可
        }

        return result;
    }

    private static void SaveCache(string cacheFilePath, List<CacheEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var model = new CacheModel { Version = CacheVersion, Jars = entries };
            File.WriteAllText(cacheFilePath, JsonSerializer.Serialize(model));
        }
        catch
        {
            // 写不进去只影响下次启动的速度
        }
    }

    private sealed class CacheModel
    {
        public int Version { get; set; }

        public List<CacheEntry> Jars { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public string Key { get; set; } = string.Empty;

        public string JarPath { get; set; } = string.Empty;

        public List<string> Packages { get; set; } = new();
    }
}
