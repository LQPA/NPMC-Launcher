using System.IO.Compression;
using System.Text.Json;

namespace NPMC.Launcher.Core.Modpacks;

/// <summary>
/// MultiMC / Prism / HMCL 的实例导出包（<c>mmc-pack.json</c> + <c>instance.cfg</c> + 载荷目录）。
///
/// 与 mrpack / CurseForge 最大的不同：**文件全在包里**，安装只是解压，
/// 不用联网下载几百个模组。但也因此它没有"下载清单"，只能靠 mmc-pack.json 得知
/// 游戏版本与加载器版本（必须严格按这里的版本装，换版本会有模组不兼容）。
/// </summary>
public sealed record MultiMcPackManifest(
    string Name,
    string VersionLabel,
    string GameVersion,
    string? LoaderName,
    string? LoaderVersion,
    string PayloadPrefix,
    int? MaxMemoryMb,
    int? WindowWidth,
    int? WindowHeight,
    IReadOnlyList<string> PayloadFiles)
{
    /// <summary>
    /// 包内预设参数（JVM 参数 + 一键进服地址）。导入时展示一次原文，由用户知情后写入实例。
    /// </summary>
    public ModpackExtras Extras { get; init; } = ModpackExtras.Empty;

    /// <summary>包里的加载器展示文本。</summary>
    public string LoaderText => LoaderName is null
        ? "原版"
        : $"{LoaderName} {LoaderVersion}";

    /// <summary>载荷里的模组数量（只统计 mods/ 下的 .jar）。</summary>
    public int ModCount => PayloadFiles.Count(path =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        && path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 读一个 MultiMC 包的清单。失败（不是该格式 / 缺关键字段）返回 null。
    /// </summary>
    public static MultiMcPackManifest? TryRead(string archivePath)
    {
        try
        {
            if (!File.Exists(archivePath)) return null;

            using var archive = ZipFile.OpenRead(archivePath);

            var mmcJson = ReadEntry(archive, ModpackArchiveFormatDetector.MultiMcPackEntry);
            var instanceCfg = ReadEntry(archive, ModpackArchiveFormatDetector.MultiMcInstanceEntry);
            var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToList();

            // options.txt 在载荷里（.minecraft/options.txt 或 minecraft/options.txt）
            var optionsEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Replace('\\', '/').EndsWith("options.txt", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.Replace('\\', '/').Count(ch => ch == '/') == 1);

            var optionsTxt = optionsEntry is null ? null : ReadEntry(archive, optionsEntry.FullName);

            return Parse(archivePath, mmcJson, instanceCfg, entries, optionsTxt);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从已读出的文本解析（便于测试，不碰磁盘）。</summary>
    public static MultiMcPackManifest? Parse(
        string archivePath,
        string? mmcPackJson,
        string? instanceCfg,
        IReadOnlyCollection<string> entryNames,
        string? optionsTxt = null)
    {
        if (string.IsNullOrWhiteSpace(mmcPackJson) && string.IsNullOrWhiteSpace(instanceCfg))
        {
            return null;
        }

        var gameVersion = string.Empty;
        string? loaderName = null;
        string? loaderVersion = null;

        if (!string.IsNullOrWhiteSpace(mmcPackJson))
        {
            using var document = JsonDocument.Parse(mmcPackJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!document.RootElement.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // 同一个组件可能出现多次（实测 HMCL 的包把 net.minecraft 写了两次），
            // 按 uid 归并：游戏版本取第一个非空，加载器取第一个非空。
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object) continue;

                var uid = component.TryGetProperty("uid", out var uidElement)
                    ? uidElement.GetString()?.Trim()
                    : null;
                var version = component.TryGetProperty("version", out var versionElement)
                    ? versionElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(version)) continue;

                if (string.Equals(uid, "net.minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    if (gameVersion.Length == 0) gameVersion = version;
                    continue;
                }

                var mapped = MapLoaderUid(uid);
                if (mapped is not null && loaderName is null)
                {
                    loaderName = mapped;
                    loaderVersion = version;
                }
            }
        }

        if (gameVersion.Length == 0) return null;

        var cfg = ParseInstanceCfg(instanceCfg);

        // 包内预设参数：MultiMC 的标准字段是 instance.cfg 的 JvmArgs（配合 OverrideJavaArgs）；
        // 另外允许包根的 .nmpc-pack.json 补充一键进服地址。
        var extras = ModpackExtras.Merge(
            new ModpackExtras(SplitJvmArgs(cfg), ReadQuickPlayFromOptions(optionsTxt)),
            ModpackExtras.FromArchive(archivePath));

        var name = cfg.TryGetValue("name", out var instanceName) && instanceName.Length > 0
            ? instanceName
            : Path.GetFileNameWithoutExtension(archivePath);

        var payloadPrefix = DetectPayloadPrefix(entryNames);

        var payloadFiles = new List<string>();
        foreach (var entry in entryNames)
        {
            if (entry.EndsWith('/')) continue;
            if (payloadPrefix.Length > 0
                && !entry.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = payloadPrefix.Length > 0 ? entry[payloadPrefix.Length..] : entry;
            if (IsLauncherMetadata(relative)) continue;
            payloadFiles.Add(relative);
        }

        return new MultiMcPackManifest(
            Name: name,
            // instance.cfg 一般没有 version 字段（HMCL 把版本写进了 name），
            // 这时留空，让摘要不要出现"版本 MultiMC 包"这种噪声
            VersionLabel: cfg.TryGetValue("version", out var versionLabel) ? versionLabel.Trim() : string.Empty,
            GameVersion: gameVersion,
            LoaderName: loaderName,
            LoaderVersion: loaderVersion,
            PayloadPrefix: payloadPrefix,
            // 只认 MaxMemAlloc：MinMemAlloc 是"最低要求"，拿它当推荐值会把
            // 作者写的 2048 当成"建议值"，把用户的自动内存覆盖成 2048（2026-11-09 实测踩到）
            MaxMemoryMb: ReadInt(cfg, "MaxMemAlloc"),
            WindowWidth: ReadInt(cfg, "MinecraftWinWidth"),
            WindowHeight: ReadInt(cfg, "MinecraftWinHeight"),
            PayloadFiles: payloadFiles)
        {
            Extras = extras,
        };
    }

    /// <summary>MultiMC 组件 uid → 我们内部使用的加载器名。不相干的组件返回 null。</summary>
    public static string? MapLoaderUid(string uid) => uid.ToLowerInvariant() switch
    {
        "net.fabricmc.fabric-loader" => "fabric-loader",
        "org.quiltmc.quilt-loader" => "quilt-loader",
        "net.neoforged" => "neoforge",
        "net.minecraftforge" => "forge",
        _ => null,
    };

    /// <summary>载荷目录：优先 .minecraft/（HMCL、Prism 默认），其次 minecraft/。</summary>
    private static string DetectPayloadPrefix(IReadOnlyCollection<string> entries)
    {
        foreach (var candidate in new[] { ".minecraft/", "minecraft/" })
        {
            if (entries.Any(entry => entry.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>包根上的启动器元数据（不能当游戏文件解压进实例）。</summary>
    public static bool IsLauncherMetadata(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.Equals(ModpackArchiveFormatDetector.MultiMcPackEntry, StringComparison.OrdinalIgnoreCase)
               || normalized.Equals(ModpackArchiveFormatDetector.MultiMcInstanceEntry, StringComparison.OrdinalIgnoreCase)
               || normalized.Equals(".packignore", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals(".minecraft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>instance.cfg 里的 JvmArgs（这是 MultiMC 的标准字段，别的启动器也认）。</summary>
    private static IReadOnlyList<string> SplitJvmArgs(Dictionary<string, string> cfg)
    {
        if (!cfg.TryGetValue("JvmArgs", out var raw) || raw.Length == 0) return Array.Empty<string>();

        // OverrideJavaArgs 明确为 false 时，说明作者有意不用这行
        if (cfg.TryGetValue("OverrideJavaArgs", out var over) 
            && bool.TryParse(over, out var enabled) && !enabled)
        {
            return Array.Empty<string>();
        }

        return ModpackExtras.SplitArguments(raw);
    }

    /// <summary>从载荷里的 options.txt 读 quickPlayMultiplayer（1.20+ 的"一键进服"设置）。</summary>
    private static string? ReadQuickPlayFromOptions(string? optionsTxt)
    {
        if (string.IsNullOrWhiteSpace(optionsTxt)) return null;

        foreach (var raw in optionsTxt.Split('\n'))
        {
            var line = raw.Trim();
            const string prefix = "quickPlayMultiplayer:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[prefix.Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }
    private static Dictionary<string, string> ParseInstanceCfg(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0) result[key] = value;
        }

        return result;
    }

    private static int? ReadInt(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var text) && int.TryParse(text, out var number) && number > 0
            ? number
            : null;

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
