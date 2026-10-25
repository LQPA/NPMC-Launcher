namespace NPMC.Launcher.Core.Content;

/// <summary>
/// 扫描内容目录。
///
/// 约定：
///   - 禁用 = 文件名追加 <see cref="DisabledSuffix"/>（&lt;name&gt;.jar → &lt;name&gt;.jar.disabled）
///   - 以「.」开头的条目一律跳过（.trash、.DS_Store 等）
///   - 模组只认 .jar；资源包/光影认 .zip 与文件夹
/// </summary>
public static class ContentScanner
{
    public const string DisabledSuffix = ".disabled";

    /// <summary>删除时移入的回收目录名（位于内容目录内）。</summary>
    public const string TrashDirectoryName = ".trash";

    public static IReadOnlyList<ContentEntry> Scan(string directory, ContentKind kind)
    {
        if (!Directory.Exists(directory)) return Array.Empty<ContentEntry>();

        var entries = new List<ContentEntry>();

        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith('.')) continue;

            var isDirectory = Directory.Exists(path);
            var isEnabled = !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var effectiveName = isEnabled ? fileName : fileName[..^DisabledSuffix.Length];

            if (!MatchesKind(effectiveName, kind, isDirectory)) continue;

            entries.Add(new ContentEntry
            {
                Path = path,
                FileName = fileName,
                Name = StripExtension(effectiveName, kind),
                Kind = kind,
                IsEnabled = isEnabled,
                IsDirectory = isDirectory,
                SizeBytes = isDirectory ? DirectorySize(path) : SafeFileSize(path),
                ModifiedAt = isDirectory ? DirectoryTime(path) : SafeFileTime(path),
            });
        }

        return entries
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool MatchesKind(string name, ContentKind kind, bool isDirectory) => kind switch
    {
        ContentKind.Mod => !isDirectory && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
        // 资源包与光影既可能是 zip，也可能是文件夹
        _ => isDirectory
             || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
    };

    private static string StripExtension(string name, ContentKind kind)
    {
        if (kind == ContentKind.Mod && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static DateTimeOffset SafeFileTime(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch { return default; }
    }

    private static DateTimeOffset DirectoryTime(string path)
    {
        try { return new DirectoryInfo(path).LastWriteTimeUtc; }
        catch { return default; }
    }

    /// <summary>递归求和；任何一项读不到就跳过，不因为一个坏文件让整次扫描失败。</summary>
    private static long DirectorySize(string path)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += SafeFileSize(file);
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
