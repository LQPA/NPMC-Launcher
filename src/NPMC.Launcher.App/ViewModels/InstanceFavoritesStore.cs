using System.Text.Json;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 收藏实例的持久化。
///
/// 单独放一个文件（<c>&lt;root&gt;\favorites.json</c>）而不是塞进 instances.json：
/// instances.json 是 CLI、整合包导出等地方共用的数据，改它的结构会牵连一圈；
/// 而收藏纯粹是界面偏好，丢了也不影响启动，放独立文件最省事。
/// </summary>
public static class InstanceFavoritesStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static HashSet<string> Load(LauncherPaths launcher)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = PathFor(launcher);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path));
            foreach (var id in model?.Ids ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(id);
                }
            }
        }
        catch
        {
            // 文件坏了就当没有收藏，不要让界面起不来
        }

        return result;
    }

    public static void Save(LauncherPaths launcher, IEnumerable<string> ids)
    {
        try
        {
            var path = PathFor(launcher);
            var model = new Model { Ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            File.WriteAllText(path, JsonSerializer.Serialize(model, Options));
        }
        catch
        {
            // 写不进去不影响本次会话的收藏状态（内存里仍然是新的）
        }
    }

    private static string PathFor(LauncherPaths launcher) => Path.Combine(launcher.Root, "favorites.json");

    private sealed class Model
    {
        public List<string> Ids { get; set; } = new();
    }
}
