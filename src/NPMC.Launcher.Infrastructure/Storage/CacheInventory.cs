namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>某个缓存区域的占用情况。</summary>
public sealed record CacheArea(
    string Key,
    string Label,
    string Path,
    long Bytes,
    int Files,
    bool SafeToClear,
    string? Note = null);

/// <summary>清理结果：删了多少文件、回收了多少字节。</summary>
public sealed record CacheClearResult(int Files, long Bytes, IReadOnlyList<string> Failures);

/// <summary>
/// 缓存占用统计与清理。
///
/// 为什么需要：<c>&lt;root&gt;\data\cache</c> 里的整合包解压产物、Forge 安装包、安装器临时目录
/// 加起来能到 1 GB（实测 400 + 380 + 126 + 94 MB），但界面上完全看不到，用户也不知道能删。
///
/// 清理的安全性分级：
///   - 可清：icons / modpacks / cfpacks / forge / tmp —— 都是可再生缓存（下次用到会重新下载/解压）；
///   - 不可清：translations（可手工维护的译文表）、skins（用户上传的皮肤）、
///     以及 loader-versions.json 这类小索引（删了只是下次启动变慢）。
/// </summary>
public static class CacheInventory
{
    public static IReadOnlyList<CacheArea> Measure(string cacheRoot)
    {
        var areas = new List<CacheArea>
        {
            MeasureArea(cacheRoot, "icons", "图标", "缓存图标；删掉后下次浏览会重新下载", safe: true),
            MeasureArea(cacheRoot, "modpacks", "整合包解压（Modrinth）", "安装整合包时的中间产物，装完就不再用", safe: true),
            MeasureArea(cacheRoot, "cfpacks", "整合包解压（CurseForge）", "同上，CurseForge 来源", safe: true),
            MeasureArea(cacheRoot, "forge", "Forge / NeoForge 安装包", "下次装 Forge 时需要重新下载", safe: true),
            MeasureArea(cacheRoot, "tmp", "安装器临时目录", "安装过程的临时文件，可随时清", safe: true),
            MeasureArea(cacheRoot, "translations", "译文表（可手工维护）", "translations.csv 是可编辑数据，不参与清理", safe: false),
            MeasureArea(cacheRoot, "skins", "皮肤文件", "你上传的皮肤，不参与清理", safe: false),
        };

        return areas;
    }

    /// <summary>清理所有标了 safe 的区域。</summary>
    public static CacheClearResult ClearSafe(string cacheRoot)
    {
        var files = 0;
        long bytes = 0;
        var failures = new List<string>();

        foreach (var area in Measure(cacheRoot).Where(area => area.SafeToClear))
        {
            try
            {
                if (!Directory.Exists(area.Path)) continue;

                foreach (var file in Directory.EnumerateFiles(area.Path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var length = new FileInfo(file).Length;
                        File.Delete(file);
                        files++;
                        bytes += length;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{area.Label}/{System.IO.Path.GetFileName(file)}：{ex.GetBaseException().Message}");
                    }
                }

                // 清完文件顺手把空目录也去掉（根目录保留）
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(area.Path, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(path => path.Length))
                    {
                        if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                        Directory.Delete(directory);
                    }
                }
                catch
                {
                    // 空目录删不掉无所谓
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{area.Label}：{ex.GetBaseException().Message}");
            }
        }

        return new CacheClearResult(files, bytes, failures);
    }

    /// <summary>
    /// 清理安装器临时目录里超过 <paramref name="olderThan"/> 的残留。
    /// 安装器中断/崩溃时不会自己收尾，实测堆了 90 MB 以上。
    /// </summary>
    public static int CleanStaleEntries(string directory, TimeSpan olderThan)
    {
        if (!Directory.Exists(directory)) return 0;

        var removed = 0;
        var cutoff = DateTime.UtcNow - olderThan;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lastWrite = Directory.Exists(entry)
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry);

                    if (lastWrite > cutoff) continue;

                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                        removed++;
                    }
                    else
                    {
                        File.Delete(entry);
                        removed++;
                    }
                }
                catch
                {
                    // 单个条目删不掉（可能仍被占用）就跳过
                }
            }
        }
        catch
        {
            // 目录读不了就算了
        }

        return removed;
    }

    private static CacheArea MeasureArea(string cacheRoot, string name, string label, string note, bool safe)
    {
        var path = System.IO.Path.Combine(cacheRoot, name);
        long bytes = 0;
        var files = 0;

        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch
                    {
                        // 统计不到的文件忽略
                    }
                }
            }
        }
        catch
        {
            // 权限问题：按 0 显示
        }

        return new CacheArea(name, label, path, bytes, files, safe, note);
    }

    /// <summary>把字节数格式化成 1.2 GB / 345 MB / 12 KB。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:N2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        >= 1024 => $"{bytes / 1024d:N0} KB",
        _ => $"{bytes} B",
    };
}
