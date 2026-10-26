using System.Text.Json;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>一次目录 ↔ 实例列表对账的结果。</summary>
public sealed record InstanceReconcileResult(
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> MissingDirectories,
    IReadOnlyList<string> UnmanagedDirectories);

/// <summary>
/// 实例目录里的身份标记（&lt;instances&gt;\&lt;Id&gt;\.nmpc-instance.json）。
/// 用"旧 Id"来认领被改名的目录，所以标记里的 Id 是改名前的 Id，对账成功后会被重写。
/// </summary>
public sealed record InstanceMarker(string Id, string Name, string VersionId);

/// <summary>
/// 让"文件夹改名"和"启动器里的实例"保持一致。
///
/// 问题：实例 Id 就是目录名，用户在资源管理器里把 <c>instances\26.1.2</c> 改成
/// <c>instances\我的世界</c> 之后，instances.json 里还是旧 Id，列表就会指向不存在的目录。
///
/// 做法：每个实例目录里放一个 <c>.nmpc-instance.json</c> 标记（Id / Name / VersionId）。
/// 启动或刷新时扫描 instances\：
///   - 标记里的 Id 在列表里、且旧目录不在了 → 认定是文件夹被改名，把 Id 与显示名同步成新目录名；
///   - 目录在、列表里也有 → 顺手刷新标记；
///   - 两边对不上又没有标记 → 只报告，不猜（宁可不处理，也别把别人的目录收编成实例）。
/// </summary>
public static class InstanceReconciler
{
    public const string MarkerFileName = ".nmpc-instance.json";

    public static void WriteMarker(LauncherPaths paths, LauncherInstance instance)
    {
        try
        {
            var directory = Path.Combine(paths.Instances, instance.Id);
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(
                new InstanceMarker(instance.Id, instance.Name, instance.VersionId), JsonConfig.Options);

            File.WriteAllText(Path.Combine(directory, MarkerFileName), json);
        }
        catch
        {
            // 标记写不了不影响启动器自身；下次对账会再试
        }
    }

    public static InstanceReconcileResult Reconcile(LauncherPaths paths, InstanceCatalog catalog)
    {
        var renamed = new List<string>();
        var missing = new List<string>();
        var unmanaged = new List<string>();

        if (!Directory.Exists(paths.Instances))
            return new InstanceReconcileResult(renamed, missing, unmanaged);

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(paths.Instances);
        }
        catch
        {
            return new InstanceReconcileResult(renamed, missing, unmanaged);
        }

        var entries = directories
            .Select(directory => (Directory: directory, Name: Path.GetFileName(directory)))
            .Select(entry => (entry.Directory, entry.Name, Marker: ReadMarker(entry.Directory)))
            .ToList();

        var markersById = new Dictionary<string, (string DirectoryName, InstanceMarker Marker)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.Marker is { } marker && !markersById.ContainsKey(marker.Id))
                markersById[marker.Id] = (entry.Name, marker);
        }

        var knownIds = new HashSet<string>(
            catalog.Instances.Select(instance => instance.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var instance in catalog.Instances.ToList())
        {
            var directory = Path.Combine(paths.Instances, instance.Id);

            if (Directory.Exists(directory))
            {
                WriteMarker(paths, instance);
                continue;
            }

            // 旧目录不在了：看有没有哪个新目录里的标记自称是它
            if (markersById.TryGetValue(instance.Id, out var found)
                && !knownIds.Contains(found.DirectoryName)
                && !string.Equals(found.DirectoryName, instance.Id, StringComparison.OrdinalIgnoreCase))
            {
                var moved = catalog.Relocate(instance.Id, found.DirectoryName, found.DirectoryName);

                if (moved is not null)
                {
                    renamed.Add($"{instance.Id} → {moved.Id}");
                    knownIds.Remove(instance.Id);
                    knownIds.Add(moved.Id);
                    WriteMarker(paths, moved);
                    continue;
                }
            }

            missing.Add(instance.Id);
        }

        // 对账后仍不属于任何实例的目录（没有标记、或标记指向不存在的实例）：只报告
        var finalIds = new HashSet<string>(
            catalog.Instances.Select(instance => instance.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (finalIds.Contains(entry.Name)) continue;

            // 这个目录的标记指向列表里的某个实例（例如重复拷贝出来的同一实例）：也算未纳管
            unmanaged.Add(entry.Name);
        }

        return new InstanceReconcileResult(renamed, missing, unmanaged);
    }

    private static InstanceMarker? ReadMarker(string directory)
    {
        try
        {
            var path = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<InstanceMarker>(json, JsonConfig.Options);
        }
        catch
        {
            return null;
        }
    }
}
