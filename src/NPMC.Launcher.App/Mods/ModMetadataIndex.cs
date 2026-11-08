using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.App.Mods;

/// <summary>依赖声明的强弱。</summary>
public enum ModDependencyKind
{
    /// <summary>必需：缺失时加载器会直接拒绝这个模组。</summary>
    Required,

    /// <summary>可选：缺失时加载器只打一条 WARN，但模组自己可能在运行期崩。</summary>
    Optional,

    /// <summary>不兼容声明（fabric 的 breaks 等）。</summary>
    Incompatible,

    /// <summary>其他（discouraged / 未知）。</summary>
    Other,
}

/// <summary>一条模组依赖声明。</summary>
public sealed record ModDependency(string ModId, string VersionRange, ModDependencyKind Kind)
{
    public bool IsRequired => Kind == ModDependencyKind.Required;
}

/// <summary>一个 jar 里声明的一个模组。</summary>
public sealed record ModMetadata(
    string ModId,
    string DisplayName,
    string Version,
    string JarPath,
    IReadOnlyList<ModDependency> Dependencies)
{
    public string FileName => Path.GetFileName(JarPath);
}

/// <summary>
/// mods 目录的只读元数据索引。
///
/// 为什么需要它：加载器在崩溃报告里给出的依赖信息是**最表层**的那一条
/// （例：iris_shader_folder requires oculus），而真实断裂点可能在更下面一层
/// （oculus 自己缺 embeddium）。只靠正则匹配日志无法把这两者区分开，
/// 必须知道 mods 目录里到底有哪些 modId、谁依赖谁。
/// </summary>
public sealed class ModIndex
{
    /// <summary>这些 id 由加载器/游戏本身提供，不需要 jar。</summary>
    private static readonly HashSet<string> BuiltIn =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "minecraft", "forge", "neoforge", "fabricloader", "fabric", "quilt_loader",
            "java", "javafml", "lowcodefml", "mcp", "modlauncher", "mixinextras",
        };

    private readonly Dictionary<string, ModMetadata> _byId;
    private readonly Dictionary<string, List<ModMetadata>> _dependents;
    private readonly List<string> _allJars;
    private readonly Dictionary<string, (string? JarPath, string? ModId)> _classCache =
        new(StringComparer.Ordinal);

    /// <summary>包名 → jar 索引（懒建：只有在真要反查类名时才扫 jar，避免拖慢正常启动）。</summary>
    private readonly Lazy<ClassIndex>? _classIndex;

    public ModIndex(
        IEnumerable<ModMetadata> mods,
        IEnumerable<string>? allJars = null,
        Func<ClassIndex>? classIndexFactory = null)
    {
        Mods = mods.ToList();
        _byId = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        _dependents = new Dictionary<string, List<ModMetadata>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Mods)
        {
            // 同一个 modId 出现多份时保留第一条；重复本身由「模组重复安装」规则处理
            _byId.TryAdd(mod.ModId, mod);
            foreach (var dependency in mod.Dependencies)
            {
                if (!_dependents.TryGetValue(dependency.ModId, out var list))
                {
                    list = new List<ModMetadata>();
                    _dependents[dependency.ModId] = list;
                }

                if (!list.Contains(mod))
                {
                    list.Add(mod);
                }
            }
        }

        _allJars = (allJars ?? Mods.Select(m => m.JarPath).Distinct(StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (classIndexFactory is not null)
        {
            _classIndex = new Lazy<ClassIndex>(classIndexFactory);
        }
    }

    public static ModIndex Empty { get; } = new(Array.Empty<ModMetadata>());

    public IReadOnlyList<ModMetadata> Mods { get; }

    public bool Contains(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }

        return BuiltIn.Contains(modId) || _byId.ContainsKey(modId);
    }

    public bool IsBuiltIn(string? modId) => !string.IsNullOrWhiteSpace(modId) && BuiltIn.Contains(modId);

    public ModMetadata? Find(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return null;
        }

        return _byId.TryGetValue(modId, out var mod) ? mod : null;
    }

    /// <summary>谁声明依赖了 <paramref name="modId"/>。</summary>
    public IReadOnlyList<ModMetadata> DependentsOf(string modId)
    {
        return _dependents.TryGetValue(modId, out var list) ? list : Array.Empty<ModMetadata>();
    }

    /// <summary>把 modId 渲染成「显示名（文件名）」，让建议能落到具体 jar。</summary>
    public string Describe(string modId)
    {
        var mod = Find(modId);
        return mod is null ? modId : $"{mod.DisplayName}（{mod.FileName}）";
    }

    /// <summary>
    /// 在 mods 目录的 jar 里找回提供某个类的 jar。
    /// 只读 zip 中央目录、不解压，几十~几百个 jar 通常在百毫秒级。
    /// </summary>
    public bool TryFindProvider(string className, out string jarPath, out ModMetadata? mod)
    {
        jarPath = string.Empty;
        mod = null;
        var entryName = NormalizeEntryName(className);
        if (entryName.Length == 0)
        {
            return false;
        }

        if (_classCache.TryGetValue(entryName, out var cached))
        {
            jarPath = cached.JarPath ?? string.Empty;
            mod = cached.ModId is null ? null : Find(cached.ModId);
            return cached.JarPath is not null;
        }

        // 有包名索引时先问它，作用有两个：
        //   1) 索引里连这个类的包前缀都没有 ⇒ 任何 jar 都不可能提供它，直接给出否定（省掉几百个 zip）；
        //   2) 有前缀 ⇒ 知道最可能的那个 jar，只打开它确认一次条目是否真的存在。
        //      确认不到仍然退回全量扫描（包名匹配不等于这个类一定在里面）。
        try
        {
            if (_classIndex?.Value is { } classIndex)
            {
                if (!classIndex.TryFindProvider(entryName, out var indexedJar, out _))
                {
                    _classCache[entryName] = (null, null);
                    return false;
                }

                if (JarContains(indexedJar, entryName))
                {
                    var indexedOwner = Mods.FirstOrDefault(m =>
                        string.Equals(m.JarPath, indexedJar, StringComparison.OrdinalIgnoreCase));
                    _classCache[entryName] = (indexedJar, indexedOwner?.ModId);
                    jarPath = indexedJar;
                    mod = indexedOwner;
                    return true;
                }
            }
        }
        catch
        {
            // 索引建不起来（权限/损坏）就退回逐 jar 扫描
        }

        foreach (var jar in _allJars)
        {
            if (JarContains(jar, entryName))
            {
                var owner = Mods.FirstOrDefault(m =>
                    string.Equals(m.JarPath, jar, StringComparison.OrdinalIgnoreCase));
                _classCache[entryName] = (jar, owner?.ModId);
                jarPath = jar;
                mod = owner;
                return true;
            }
        }

        _classCache[entryName] = (null, null);
        return false;
    }

    private static bool JarContains(string jarPath, string entryName)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // jar 被占用/损坏都不影响诊断主线
        }

        return false;
    }

    private static string NormalizeEntryName(string className)
    {
        var name = className.Trim().Replace('\\', '/');
        while (name.Contains("//", StringComparison.Ordinal))
        {
            name = name.Replace("//", "/", StringComparison.Ordinal);
        }

        name = name.TrimStart('/').Replace('.', '/');
        if (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^6];
        }

        return name.Length == 0 ? string.Empty : name + ".class";
    }

    /// <summary>
    /// 已知的「包名 → 前置模组」对照表。
    ///
    /// 这一类反查不可能从缺失的 jar 里查出来（jar 都不在），只能靠内置知识。
    /// 命中时只作为「高度可能」的结论使用，措辞上不能写成确定事实。
    /// </summary>
    private static readonly (string Prefix, string DisplayName, string[] ModIds)[] KnownPackages =
    {
        ("net/caffeinemc/mods/sodium", "Sodium / Embeddium", new[] { "sodium", "embeddium" }),
        ("me/jellysquid/mods/sodium", "Sodium", new[] { "sodium" }),
        ("org/embeddedt/embeddium", "Embeddium", new[] { "embeddium" }),
        ("net/irisshaders/iris", "Oculus / Iris", new[] { "oculus", "iris" }),
        ("dev/architectury", "Architectury API", new[] { "architectury" }),
        ("net/minecraftforge/client", "Forge 客户端", Array.Empty<string>()),
        ("mezz/jei", "JEI", new[] { "jei" }),
        ("com/simibubi/create", "Create", new[] { "create" }),
        ("top/theillusivec4/curios", "Curios API", new[] { "curios" }),
        ("vazkii/botania", "Botania", new[] { "botania" }),
        ("thedarkcolour/kotlinforforge", "Kotlin for Forge", new[] { "kotlinforforge" }),
        ("mekanism", "Mekanism", new[] { "mekanism" }),
        ("com/electronwill/nightconfig", "NightConfig", new[] { "nightconfig" }),
        ("net/creeperhost", "CreeperHost 库", Array.Empty<string>()),
        ("org/apache/maven", "Maven 解析库", Array.Empty<string>()),
        ("io/netty", "Netty", Array.Empty<string>()),
        ("org/lwjgl", "LWJGL", Array.Empty<string>()),
        ("com/mojang", "游戏本体", Array.Empty<string>()),
        ("net/minecraft", "游戏本体", Array.Empty<string>()),
    };

    /// <summary>
    /// 反查某个类可能的提供方。先看已装 jar（确定），再看内置包名表（推测）。
    /// </summary>
    public ClassOwner ExplainClass(string className)
    {
        if (TryFindProvider(className, out var jarPath, out var mod))
        {
            return new ClassOwner(className, true, mod?.ModId, mod?.DisplayName, jarPath, Array.Empty<string>());
        }

        var normalized = "/" + className.Trim().Replace('\\', '/').Replace('.', '/').TrimStart('/');
        foreach (var (prefix, displayName, modIds) in KnownPackages)
        {
            if (normalized.Contains("/" + prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new ClassOwner(className, false, modIds.FirstOrDefault(), displayName, null, modIds);
            }
        }

        return new ClassOwner(className, false, null, null, null, Array.Empty<string>());
    }
}

/// <summary>一个类由谁提供。</summary>
public sealed record ClassOwner(
    string ClassName,
    bool FoundInMods,
    string? ModId,
    string? DisplayName,
    string? JarPath,
    IReadOnlyList<string> CandidateModIds)
{
    public bool HasKnownOwner => !string.IsNullOrWhiteSpace(DisplayName);
}

/// <summary>从 mods 目录构建 <see cref="ModIndex"/>，结果按 jar 的 (大小, 修改时间) 缓存。</summary>
public static class ModMetadataIndex
{
    private const int CacheVersion = 1;

    public static ModIndex Build(
        string? modsDirectory,
        string? cacheFilePath = null,
        string? classIndexCachePath = null)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            return ModIndex.Empty;
        }

        var candidates = new List<FileInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            {
                candidates.Add(new FileInfo(path));
            }
        }
        catch
        {
            return ModIndex.Empty;
        }

        var cache = LoadCache(cacheFilePath);
        var mods = new List<ModMetadata>();
        var freshEntries = new List<CacheEntry>();
        var reused = 0;

        foreach (var file in candidates)
        {
            var key = $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            if (cache.TryGetValue(key, out var entry))
            {
                reused++;
                freshEntries.Add(entry);
                mods.AddRange(entry.Mods.Select(m => m.ToMetadata(file.FullName)));
                continue;
            }

            var parsed = ParseJar(file.FullName);
            if (parsed.Count == 0)
            {
                continue;
            }

            var newEntry = new CacheEntry
            {
                Key = key,
                Mods = parsed.Select(ModCacheEntry.From).ToList(),
            };
            freshEntries.Add(newEntry);
            mods.AddRange(parsed.Select(m => m with { JarPath = file.FullName }));
        }

        if (cacheFilePath is not null && (reused != candidates.Count || !File.Exists(cacheFilePath)))
        {
            SaveCache(cacheFilePath, freshEntries);
        }

        return new ModIndex(
            mods,
            candidates.Select(c => c.FullName),
            // 包名索引懒建：只有诊断真要反查类名时才会扫 jar
            classIndexCachePath is null
                ? null
                : () => ClassIndex.Build(candidates.Select(c => c.FullName), classIndexCachePath));
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
            var json = File.ReadAllText(cacheFilePath);
            var model = JsonSerializer.Deserialize<CacheModel>(json);
            if (model is not { Version: CacheVersion })
            {
                return result;
            }

            foreach (var entry in model.Entries)
            {
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    result[entry.Key] = entry;
                }
            }
        }
        catch
        {
            // 缓存坏了就当没有，重新扫一遍
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

            var model = new CacheModel { Version = CacheVersion, Entries = entries };
            File.WriteAllText(
                cacheFilePath,
                JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            // 缓存写不进去不影响诊断
        }
    }

    private static List<ModMetadata> ParseJar(string jarPath)
    {
        var result = new List<ModMetadata>();
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var modsToml = ReadEntry(archive, "META-INF/neoforge.mods.toml")
                           ?? ReadEntry(archive, "META-INF/mods.toml");
            if (modsToml is not null)
            {
                ParseToml(modsToml, jarPath, result);
            }

            var fabric = ReadEntry(archive, "fabric.mod.json");
            if (fabric is not null)
            {
                ParseFabricJson(fabric, jarPath, result, "fabric");
            }

            var quilt = ReadEntry(archive, "quilt.mod.json");
            if (quilt is not null)
            {
                ParseFabricJson(quilt, jarPath, result, "quilt");
            }
        }
        catch
        {
            // 损坏的 jar 由「jar 损坏」规则处理
        }

        return result;
    }

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        foreach (var entry in archive.Entries)
        {
            if (!string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return null;
    }

    /// <summary>
    /// 极简 TOML：只认 [[mods]] 与 [[dependencies.&lt;modId&gt;]] 两种表数组，
    /// 以及 key = "value" / key = true 这两种赋值。mods.toml 是固定格式，
    /// 为避免引入第三方依赖，这里不做完整 TOML 支持。
    /// </summary>
    private static void ParseToml(string text, string jarPath, List<ModMetadata> output)
    {
        var mods = new List<(string ModId, string DisplayName, string Version)>();
        var dependencies = new Dictionary<string, List<ModDependency>>(StringComparer.OrdinalIgnoreCase);

        var currentMod = -1;
        string? currentDepOwner = null;
        string? pendingDepModId = null;
        string pendingDepRange = "*";
        var pendingDepKind = ModDependencyKind.Required;

        void FlushDependency()
        {
            if (currentDepOwner is null || pendingDepModId is null)
            {
                return;
            }

            if (!dependencies.TryGetValue(currentDepOwner, out var list))
            {
                list = new List<ModDependency>();
                dependencies[currentDepOwner] = list;
            }

            list.Add(new ModDependency(pendingDepModId, pendingDepRange, pendingDepKind));
            pendingDepModId = null;
            pendingDepRange = "*";
            pendingDepKind = ModDependencyKind.Required;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                FlushDependency();
                var name = line.Trim('[', ']', ' ', '\t', '\r');
                if (name.Equals("mods", StringComparison.OrdinalIgnoreCase))
                {
                    mods.Add((string.Empty, string.Empty, string.Empty));
                    currentMod = mods.Count - 1;
                    currentDepOwner = null;
                }
                else if (name.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
                {
                    currentDepOwner = name["dependencies.".Length..].Trim().Trim('"');
                    currentMod = -1;
                }
                else
                {
                    currentMod = -1;
                    currentDepOwner = null;
                }

                continue;
            }

            if (line.StartsWith("["))
            {
                FlushDependency();
                currentMod = -1;
                currentDepOwner = null;
                continue;
            }

            var (key, value) = SplitAssignment(line);
            if (key is null)
            {
                continue;
            }

            if (currentDepOwner is not null)
            {
                switch (key.ToLowerInvariant())
                {
                    case "modid":
                        pendingDepModId = value;
                        break;
                    case "versionrange":
                        pendingDepRange = string.IsNullOrWhiteSpace(value) ? "*" : value;
                        break;
                    case "mandatory":
                        pendingDepKind = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                            ? ModDependencyKind.Required
                            : ModDependencyKind.Optional;
                        break;
                    case "type":
                        pendingDepKind = value.ToLowerInvariant() switch
                        {
                            "required" => ModDependencyKind.Required,
                            "optional" => ModDependencyKind.Optional,
                            "incompatible" => ModDependencyKind.Incompatible,
                            _ => ModDependencyKind.Other,
                        };
                        break;
                }

                continue;
            }

            if (currentMod < 0)
            {
                continue;
            }

            var (modId, displayName, version) = mods[currentMod];
            switch (key.ToLowerInvariant())
            {
                case "modid":
                    mods[currentMod] = (value, displayName, version);
                    break;
                case "displayname":
                    mods[currentMod] = (modId, value, version);
                    break;
                case "version":
                    mods[currentMod] = (modId, displayName, value);
                    break;
            }
        }

        FlushDependency();

        foreach (var (modId, displayName, version) in mods)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                continue;
            }

            IReadOnlyList<ModDependency> deps = dependencies.TryGetValue(modId, out var list)
                ? list
                : Array.Empty<ModDependency>();

            output.Add(new ModMetadata(
                modId.Trim(),
                string.IsNullOrWhiteSpace(displayName) ? modId.Trim() : displayName.Trim(),
                NormalizeVersion(version),
                jarPath,
                deps.ToList()));
        }
    }

    private static void ParseFabricJson(string json, string jarPath, List<ModMetadata> output, string flavour)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string modId;
        string displayName;
        string version = string.Empty;
        JsonElement depends = default;
        JsonElement? loader = null;

        if (flavour == "quilt" && root.TryGetProperty("quilt_loader", out var quiltLoader))
        {
            loader = quiltLoader;
            modId = GetString(quiltLoader, "id") ?? string.Empty;
            version = GetString(quiltLoader, "version") ?? string.Empty;
            displayName = modId;
            if (quiltLoader.TryGetProperty("metadata", out var metadata))
            {
                displayName = GetString(metadata, "name") ?? modId;
            }

            if (quiltLoader.TryGetProperty("depends", out var quiltDepends))
            {
                depends = quiltDepends;
            }
        }
        else
        {
            modId = GetString(root, "id") ?? string.Empty;
            displayName = GetString(root, "name") ?? modId;
            version = GetString(root, "version") ?? string.Empty;
            if (root.TryGetProperty("depends", out var fabricDepends))
            {
                depends = fabricDepends;
            }
        }

        if (string.IsNullOrWhiteSpace(modId))
        {
            return;
        }

        var deps = new List<ModDependency>();
        AddJsonDependencies(depends, ModDependencyKind.Required, deps);

        if (loader is { } l && l.TryGetProperty("breaks", out var quiltBreaks))
        {
            AddJsonDependencies(quiltBreaks, ModDependencyKind.Incompatible, deps);
        }

        if (root.TryGetProperty("breaks", out var fabricBreaks))
        {
            AddJsonDependencies(fabricBreaks, ModDependencyKind.Incompatible, deps);
        }

        output.Add(new ModMetadata(
            modId.Trim(),
            string.IsNullOrWhiteSpace(displayName) ? modId.Trim() : displayName.Trim(),
            NormalizeVersion(version),
            jarPath,
            deps));
    }

    private static void AddJsonDependencies(JsonElement element, ModDependencyKind kind, List<ModDependency> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var range = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? "*"
                        : "*";
                    output.Add(new ModDependency(property.Name, range, kind));
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    string? id = null;
                    var range = "*";
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        id = item.GetString();
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        id = GetString(item, "id");
                        range = GetString(item, "versions") ?? "*";
                    }

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        output.Add(new ModDependency(id, range, kind));
                    }
                }

                break;
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>模组元数据里的 version 常写成 ${file.jarVersion} 这类占位符，取不到就留空。</summary>
    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Contains("${", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return version.Trim();
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (c == '#' && !inString)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static (string? Key, string Value) SplitAssignment(string line)
    {
        var index = line.IndexOf('=');
        if (index <= 0)
        {
            return (null, string.Empty);
        }

        var key = line[..index].Trim();
        var value = line[(index + 1)..].Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            value = value[1..^1];
        }

        return (key, value);
    }

    private sealed class CacheModel
    {
        public int Version { get; set; }

        public List<CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public string Key { get; set; } = string.Empty;

        public List<ModCacheEntry> Mods { get; set; } = new();
    }

    private sealed class ModCacheEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public List<DependencyCacheEntry> Deps { get; set; } = new();

        public static ModCacheEntry From(ModMetadata mod) => new()
        {
            Id = mod.ModId,
            Name = mod.DisplayName,
            Version = mod.Version,
            Deps = mod.Dependencies
                .Select(d => new DependencyCacheEntry { Id = d.ModId, Range = d.VersionRange, Kind = (int)d.Kind })
                .ToList(),
        };

        public ModMetadata ToMetadata(string jarPath) => new(
            Id,
            Name,
            Version,
            jarPath,
            Deps.Select(d => new ModDependency(d.Id, d.Range, (ModDependencyKind)d.Kind)).ToList());
    }

    private sealed class DependencyCacheEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Range { get; set; } = "*";

        public int Kind { get; set; }
    }
}
