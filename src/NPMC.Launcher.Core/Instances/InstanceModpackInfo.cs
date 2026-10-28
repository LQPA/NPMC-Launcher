using System.Text.Json;
using NPMC.Launcher.Core.Modpacks;

namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 实例的整合包来源与“缺少哪些模组”。
///
/// 存在实例目录的 <c>.nmpc-modpack.json</c>：不影响实例启动，删掉也只是少一个标记。
/// </summary>
/// <summary>
/// 整合包自带的一个文件。
///
/// <paramref name="FileName"/> 是逻辑文件名（禁用文件已去掉 `.disabled`）。
/// </summary>
public sealed record PackFileEntry(string FileName, string Sha1 = "")
{
    /// <summary>
    /// 是否来自整合包清单（`files[]` 或作者禁止下载、由用户手动补的那个文件）。
    ///
    /// 只有这类文件才能按整合包来源（Modrinth / CurseForge）打徽标；
    /// `overrides/` 里直接打包进来的自制 / 三方模组平台根本不认识，必须保持 `?`。
    /// 旧版 JSON 没有这个字段 → 默认 true，与以前"清单里的都算"的行为一致。
    /// </summary>
    public bool FromIndex { get; init; } = true;
}

public sealed record InstanceModpackInfo(
    string Source,
    string ProjectId,
    string Name,
    string Version,
    IReadOnlyList<BlockedModFile> Missing)
{
    /// <summary>整合包自带文件（安装时记录）；更新检查只跳过这些，用户后加的仍检查。</summary>
    public IReadOnlyList<PackFileEntry> PackFiles { get; init; } = Array.Empty<PackFileEntry>();

    public bool IsCurseForge =>
        string.Equals(Source, "curseforge", StringComparison.OrdinalIgnoreCase);

    public string SourceText => IsCurseForge ? "CurseForge" : "Modrinth";

    public bool HasMissing => Missing.Count > 0;

    public string SummaryText => HasMissing
        ? $"{SourceText} 整合包 · 缺少 {DescribeMissing()}"
        : $"{SourceText} 整合包 · 文件完整";

    /// <summary>按目标目录分类型统计缺失文件（模组 / 资源包 / 光影），只列非零项。</summary>
    private string DescribeMissing()
    {
        var parts = new List<string>();

        var mods = Missing.Count(file => string.Equals(file.TargetFolder, "mods", StringComparison.OrdinalIgnoreCase));
        var resourcePacks = Missing.Count(file => string.Equals(file.TargetFolder, "resourcepacks", StringComparison.OrdinalIgnoreCase));
        var shaderPacks = Missing.Count(file => string.Equals(file.TargetFolder, "shaderpacks", StringComparison.OrdinalIgnoreCase));

        if (mods > 0) parts.Add($"{mods} 个模组");
        if (resourcePacks > 0) parts.Add($"{resourcePacks} 个资源包");
        if (shaderPacks > 0) parts.Add($"{shaderPacks} 个光影");

        return parts.Count == 0 ? $"{Missing.Count} 个文件" : string.Join("、", parts);
    }
}

public static class InstanceModpackStore
{
    public const string FileName = ".nmpc-modpack.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string PathFor(string gameDirectory) => Path.Combine(gameDirectory, FileName);

    public static InstanceModpackInfo? Load(string gameDirectory)
    {
        try
        {
            var path = PathFor(gameDirectory);
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<InstanceModpackInfo>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string gameDirectory, InstanceModpackInfo info)
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);
            File.WriteAllText(PathFor(gameDirectory), JsonSerializer.Serialize(info, Options));
        }
        catch
        {
            // 标记失败不影响游戏，静默
        }
    }

    public static void Delete(string gameDirectory)
    {
        try
        {
            var path = PathFor(gameDirectory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
