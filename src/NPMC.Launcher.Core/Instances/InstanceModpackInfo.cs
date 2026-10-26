using System.Text.Json;
using NPMC.Launcher.Core.Modpacks;

namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 实例的整合包来源与“缺少哪些模组”。
///
/// 存在实例目录的 <c>.nmpc-modpack.json</c>：不影响实例启动，删掉也只是少一个标记。
/// </summary>
public sealed record InstanceModpackInfo(
    string Source,
    string ProjectId,
    string Name,
    string Version,
    IReadOnlyList<BlockedModFile> Missing)
{
    public bool IsCurseForge =>
        string.Equals(Source, "curseforge", StringComparison.OrdinalIgnoreCase);

    public string SourceText => IsCurseForge ? "CurseForge" : "Modrinth";

    public bool HasMissing => Missing.Count > 0;

    public string SummaryText => HasMissing
        ? $"{SourceText} 整合包 · 缺少 {Missing.Count} 个模组"
        : $"{SourceText} 整合包 · 模组完整";
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
