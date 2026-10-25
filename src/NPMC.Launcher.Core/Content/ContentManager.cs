namespace NPMC.Launcher.Core.Content;

/// <summary>
/// 内容的启用/禁用/删除。
///
/// 两个安全考虑：
///   1. **路径必须落在目标目录内**。这些操作会移动/删除文件，绝不能因为传入了一条
///      奇怪的路径就动到目录外的东西（<see cref="EnsureInside"/>）。
///   2. **删除默认进 .trash 而不是真删**。删错一个模组的代价远高于多占一点磁盘，
///      移入回收目录后用户随时能在资源管理器里捡回来。
/// </summary>
public sealed class ContentManager
{
    /// <summary>切换启用状态，返回更新后的条目。</summary>
    public ContentEntry Toggle(ContentEntry entry, string directory)
    {
        var path = EnsureInside(directory, entry.Path);
        var isDirectory = Directory.Exists(path);

        if (entry.IsEnabled)
        {
            var target = path + ContentScanner.DisabledSuffix;
            if (Exists(target))
                throw new InvalidOperationException($"已存在同名禁用条目：{Path.GetFileName(target)}");

            Move(path, target, isDirectory);
            return entry with
            {
                Path = target,
                FileName = Path.GetFileName(target),
                IsEnabled = false,
            };
        }

        if (!path.EndsWith(ContentScanner.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"条目不是禁用状态，无法启用：{Path.GetFileName(path)}");

        var enabled = path[..^ContentScanner.DisabledSuffix.Length];
        if (Exists(enabled))
            throw new InvalidOperationException($"已存在同名条目：{Path.GetFileName(enabled)}");

        Move(path, enabled, isDirectory);
        return entry with
        {
            Path = enabled,
            FileName = Path.GetFileName(enabled),
            IsEnabled = true,
        };
    }

    /// <summary>
    /// 删除。默认移入内容目录下的 .trash（可恢复），permanent 为 true 时彻底删除。
    /// 返回文件最终所在位置（或已被删除的路径）。
    /// </summary>
    public string Delete(ContentEntry entry, string directory, bool permanent = false)
    {
        var path = EnsureInside(directory, entry.Path);
        var isDirectory = Directory.Exists(path);

        if (permanent)
        {
            if (isDirectory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return path;
        }

        var trash = Path.Combine(Path.GetFullPath(directory), ContentScanner.TrashDirectoryName);
        Directory.CreateDirectory(trash);

        var target = Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(path)}");
        var suffix = 1;
        while (Exists(target))
            target = Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{suffix++}-{Path.GetFileName(path)}");

        Move(path, target, isDirectory);
        return target;
    }

    /// <summary>把目录下的内容一次性全部禁用/启用，用于批量操作。</summary>
    public int ToggleAll(string directory, ContentKind kind, bool enable)
    {
        var changed = 0;
        foreach (var entry in ContentScanner.Scan(directory, kind))
        {
            if (entry.IsEnabled == enable) continue;
            Toggle(entry, directory);
            changed++;
        }

        return changed;
    }

    /// <summary>校验路径确实位于指定目录之下，防止越界操作。</summary>
    private static string EnsureInside(string directory, string path)
    {
        var root = Path.GetFullPath(directory);
        var full = Path.GetFullPath(path);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"路径不在 {root} 内，已拒绝操作：{full}");

        return full;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
