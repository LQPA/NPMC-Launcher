using System.Text;

namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 实例 Id 的生成与校验。
/// Id 同时是实例文件夹名，所以必须是合法的 Windows 目录名；
/// 但显示名可以随便写（含中文、空格），两者解耦。
/// </summary>
public static class InstanceId
{
    private static readonly char[] Illegal = Path.GetInvalidFileNameChars();

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length > 64) return false;
        if (id is "." or "..") return false;
        if (id.IndexOfAny(Illegal) >= 0) return false;
        // Windows 不允许目录名以点或空格结尾
        if (id.EndsWith('.') || id.EndsWith(' ')) return false;
        return true;
    }

    /// <summary>
    /// 把显示名转成目录名。保留中文等 Unicode 字符（Windows 目录名支持），
    /// 只把非法字符和空格替换成连字符。
    /// </summary>
    public static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "instance";

        var builder = new StringBuilder();
        foreach (var ch in name.Trim())
            builder.Append(Array.IndexOf(Illegal, ch) >= 0 || ch == ' ' ? '-' : ch);

        var slug = builder.ToString().Trim('-', '.');
        if (slug.Length > 64) slug = slug[..64].TrimEnd('-', '.');

        return slug.Length == 0 ? "instance" : slug;
    }

    /// <summary>在同名时追加 -2 / -3 …… 保证唯一。</summary>
    public static string MakeUnique(string name, IEnumerable<string> existingIds)
    {
        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        var baseId = Slug(name);
        if (!taken.Contains(baseId)) return baseId;

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!taken.Contains(candidate)) return candidate;
        }

        var fallback = $"{baseId}-{Guid.NewGuid():N}";
        return fallback.Length <= 64 ? fallback : fallback[..64];
    }
}
