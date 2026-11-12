using System.IO.Compression;

namespace NPMC.Launcher.Core.Worlds;

/// <summary>一次地图解压的结果。</summary>
public sealed record MapExtractResult(
    string WorldDirectory,
    string WorldName,
    int ExtractedFiles,
    IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;
}

/// <summary>
/// 把 CurseForge 的存档 zip 解压成 <c>saves/&lt;地图名&gt;/</c>。
///
/// 三种常见的包结构都要处理，实测 CF 地图里三种都出现过：
///   1. 直接就是世界内容：<c>level.dat</c> 在压缩包根上；
///   2. 外面套一层目录：<c>MyMap/level.dat</c>（作者直接打包了存档文件夹）；
///   3. 套了多层：<c>MyMap/MyMap/level.dat</c>（Windows 右键"压缩到 zip"造成的）。
///
/// 判定依据是 <c>level.dat</c> 所在深度 —— Minecraft 只认 <c>saves/&lt;dir&gt;/level.dat</c>，
/// 多一层目录会让存档在世界列表里不出现。仅当整个包里只有一个 level.dat 时才剥壳，
/// 多个存档（一个包里塞好几张图）时按原样展开。
/// </summary>
public static class MapExtractor
{
    private const string LevelFile = "level.dat";

    /// <summary>解压到 <paramref name="savesDirectory"/> 下，返回最终的世界目录。</summary>
    /// <param name="worldName">
    /// 期望的世界文件夹名；为空或与现有目录冲突时自动加序号。调用方一般传地图标题。
    /// </param>
    public static Task<MapExtractResult> ExtractAsync(
        string archivePath,
        string savesDirectory,
        string worldName,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("存档包不存在", archivePath);

            using var archive = ZipFile.OpenRead(archivePath);

            var entries = archive.Entries
                .Where(entry => entry.FullName.Length > 0 && !entry.FullName.EndsWith('/'))
                .ToList();

            if (entries.Count == 0)
                return new MapExtractResult("", worldName, 0, new[] { "压缩包是空的" });

            // 找到所有 level.dat，据此决定要不要剥壳
            var levelDats = entries
                .Select(entry => Normalize(entry.FullName))
                .Where(path => string.Equals(Path.GetFileName(path), LevelFile, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var prefix = "";
            if (levelDats.Count == 1)
            {
                var directory = Path.GetDirectoryName(levelDats[0])?.Replace('\\', '/') ?? "";
                if (directory.Length > 0)
                {
                    // 只有"整个包都在这一层之下"时才能整体剥掉，否则保留原样
                    var allInside = entries.All(entry =>
                        Normalize(entry.FullName).StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase));

                    if (allInside) prefix = directory + "/";
                }
            }

            Directory.CreateDirectory(savesDirectory);

            var target = UniqueDirectory(savesDirectory, Sanitize(worldName));

            var problems = new List<string>();
            var extracted = 0;
            var total = entries.Count;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // zip-slip：含 .. 段的条目直接拒绝（显式判断，不依赖后续修剪的副作用）
                if (HasParentSegment(entry.FullName))
                {
                    problems.Add($"{entry.FullName}：路径越界，已拒绝");
                    continue;
                }

                var relative = Normalize(entry.FullName);

                if (prefix.Length > 0)
                {
                    if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // 包根上的说明文件（README 等）不属于这个世界，跳过
                        continue;
                    }

                    relative = relative[prefix.Length..];
                }

                if (relative.Length == 0) continue;

                try
                {
                    var destination = SafeCombine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    extracted++;
                }
                catch (Exception ex)
                {
                    problems.Add($"{relative}：{ex.GetBaseException().Message}");
                }

                progress?.Report((extracted, total));
            }

            return new MapExtractResult(target, Path.GetFileName(target), extracted, problems);
        }, cancellationToken);

    /// <summary>统一分隔符并去掉开头的 ./ 与 /。</summary>
    private static string Normalize(string name) =>
        name.Replace('\\', '/').TrimStart('/').TrimStart('.', '/');

    /// <summary>
    /// 条目里是否含 <c>..</c> 段（zip-slip）。
    ///
    /// 必须在 Normalize **之前**判断：Normalize 的 <c>TrimStart('.', '/')</c> 会把
    /// <c>../escaped.txt</c> 修剪成 <c>escaped.txt</c>，路径因此"恰好"落在目录内 ——
    /// 结果是对的，但那只是修剪带来的副作用，不是显式校验。显式拒绝更可靠，
    /// 也能让调用方看到真实的坏条目。
    /// </summary>
    private static bool HasParentSegment(string name)
    {
        var normalized = name.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..") return true;
        }

        return false;
    }

    /// <summary>Windows 文件名非法的字符换成下划线，并去掉结尾的点与空格。</summary>
    private static string Sanitize(string name)
    {
        var cleaned = name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');

        cleaned = cleaned.Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "地图" : cleaned;
    }

    /// <summary>同名目录已存在时加 " (2)"、" (3)"…，绝不覆盖已有存档。</summary>
    private static string UniqueDirectory(string root, string name)
    {
        var candidate = Path.Combine(root, name);
        var index = 2;

        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{name} ({index})");
            index++;
        }

        return candidate;
    }

    /// <summary>拼路径并确认没有跑到目标目录外面（zip 条目来自外部，不能直接信）。</summary>
    private static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);

        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"路径越界：{relative}");
        }

        return full;
    }
}
