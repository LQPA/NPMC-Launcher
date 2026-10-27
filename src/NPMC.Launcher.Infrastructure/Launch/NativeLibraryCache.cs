using NPMC.Launcher.Core.Models;

namespace NPMC.Launcher.Infrastructure.Launch;

/// <summary>
/// LWJGL 本机库缓存目录的清理。
///
/// 背景：LWJGL 3.3 把解压出来的动态库**平铺**在
/// <c>-Dorg.lwjgl.system.SharedLibraryExtractPath</c> 指定目录下；3.4 改成
/// <c>&lt;build&gt;/&lt;arch&gt;/</c> 子目录。两者混在同一个目录里时，3.4 的平台探测会认错架构：
/// 实测 Minecraft 26.3（LWJGL 3.4.3）在 <c>versions\26.3\natives\lwjgl</c> 里留着平铺的
/// 旧 dll 时，报 <c>Platform available on classpath: windows/arm64</c>，随后 FML 的早期窗口
/// 崩在 <c>DisplayWindow.shutdownAutomaticRenderer</c>。
///
/// 因此启动前把平铺的顶层动态库删掉（子目录缓存不动）。这些都是可再生的解压产物，
/// 删掉后 LWJGL 会按新布局重新解压。
/// </summary>
public static class NativeLibraryCache
{
    private const string ExtractPathPrefix = "-Dorg.lwjgl.system.SharedLibraryExtractPath=";

    private static readonly string[] NativeExtensions = { ".dll", ".so", ".dylib" };

    /// <summary>
    /// 清掉旧布局残留，返回删除的文件数。
    /// 只在版本使用 LWJGL 3.4+（子目录布局）时动手，老版本保持原样。
    /// </summary>
    public static int RemoveStaleFlatFiles(VersionJson version, IReadOnlyList<string> jvmArguments)
    {
        if (!UsesVersionedLayout(version)) return 0;

        var removed = 0;
        foreach (var argument in jvmArguments)
        {
            if (!argument.StartsWith(ExtractPathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            removed += RemoveFlatFiles(argument[ExtractPathPrefix.Length..].Trim('"'));
        }

        return removed;
    }

    private static int RemoveFlatFiles(string directory)
    {
        var removed = 0;

        try
        {
            if (!Directory.Exists(directory)) return 0;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!NativeExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 正在被别的进程占用：下次启动再试，不影响本次
                }
            }
        }
        catch
        {
            // 目录读不了就算了，别让清理拖垮启动
        }

        return removed;
    }

    /// <summary>只有 LWJGL 3.4 起才使用 &lt;build&gt;/&lt;arch&gt;/ 子目录缓存。</summary>
    public static bool UsesVersionedLayout(VersionJson version)
    {
        foreach (var library in version.Libraries)
        {
            var parts = library.Name.Split(':');
            if (parts.Length < 3) continue;
            if (!parts[0].Equals("org.lwjgl", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[1].Equals("lwjgl", StringComparison.OrdinalIgnoreCase)) continue;

            return Version.TryParse(parts[2].Split('+')[0].Split('-')[0], out var parsed)
                   && parsed >= new Version(3, 4);
        }

        return false;
    }
}
