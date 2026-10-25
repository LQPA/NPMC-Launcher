using System.Diagnostics;
using System.Text.RegularExpressions;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Launch;

public sealed record JavaInstallation(string Path, int MajorVersion, bool IsBundled, string? Component);

/// <summary>
/// Java 运行时探测。
///
/// 两条来源：
///   1. 内置运行时 &lt;root&gt;\runtime\&lt;component&gt;\bin\java.exe（由 MojangRuntimeInstaller 安装）
///   2. 系统已安装的 JDK/JRE —— 扫描常见安装目录，暂不含注册表
///      （注册表需要 net8.0-windows TFM，等后续迁移时再补）
///
/// 版本判定用 `java -XshowSettings:properties -version` 的
/// `java.specification.version`，比解析 `-version` 字符串稳。
/// 注意 Java 8 报的是 "1.8"，本类会归一化成 8。
/// </summary>
public static class JavaLocator
{
    private static readonly Regex SpecVersionRegex =
        new(@"java\.specification\.version\s*=\s*([\d._]+)", RegexOptions.Compiled);

    /// <summary>从 java 的属性输出里解析主版本号。</summary>
    public static int? ParseMajorVersion(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;

        var match = SpecVersionRegex.Match(output);
        if (!match.Success) return null;

        var raw = match.Groups[1].Value.Replace('_', '.');
        var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // Java 8 及更早：specification.version = "1.8"
        if (parts[0] == "1" && parts.Length >= 2)
            return int.TryParse(parts[1], out var legacy) ? legacy : null;

        return int.TryParse(parts[0], out var major) ? major : null;
    }

    /// <summary>实际运行一次 java 读取主版本；失败返回 null。</summary>
    public static int? ReadMajorVersion(string javaExe, int timeoutMs = 10000)
    {
        try
        {
            var startInfo = new ProcessStartInfo(javaExe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-XshowSettings:properties");
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return null;
            }

            return ParseMajorVersion(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>内置运行时：&lt;root&gt;\runtime\&lt;component&gt;\bin\java.exe</summary>
    public static IReadOnlyList<JavaInstallation> DiscoverBundled(LauncherPaths paths)
    {
        var results = new List<JavaInstallation>();
        if (!Directory.Exists(paths.Runtime)) return results;

        foreach (var directory in Directory.EnumerateDirectories(paths.Runtime))
        {
            var javaExe = Path.Combine(directory, "bin", "java.exe");
            if (!File.Exists(javaExe)) continue;

            results.Add(new JavaInstallation(
                javaExe,
                ReadMajorVersion(javaExe) ?? 0,
                IsBundled: true,
                Component: Path.GetFileName(directory)));
        }

        return results;
    }

    /// <summary>系统里常见的 JDK 安装根目录。</summary>
    public static IEnumerable<string> EnumerateSystemRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new[]
        {
            Path.Combine(programFiles, "Eclipse Adoptium"),
            Path.Combine(programFiles, "Java"),
            Path.Combine(programFiles, "Microsoft"),
            Path.Combine(programFiles, "BellSoft"),
            Path.Combine(programFiles, "Zulu"),
            Path.Combine(programFiles, "Amazon Corretto"),
            Path.Combine(programFiles, "Semeru"),
            Path.Combine(programFiles, "AdoptOpenJDK"),
            Path.Combine(localAppData, "Programs", "Eclipse Adoptium"),
            // 官方启动器自带的运行时（组件名形如 java-runtime-gamma）
            Path.Combine(localAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe",
                "LocalCache", "Local", "runtime"),
        };

        foreach (var root in roots)
            if (Directory.Exists(root)) yield return root;
    }

    /// <summary>合并内置与系统 Java（按路径去重）。</summary>
    public static IReadOnlyList<JavaInstallation> Discover(LauncherPaths paths, int maxSystemCandidates = 12)
    {
        var results = new List<JavaInstallation>(DiscoverBundled(paths));
        var seen = results.Select(r => Normalize(r.Path)).ToHashSet(StringComparer.Ordinal);

        foreach (var java in DiscoverSystem(maxSystemCandidates))
        {
            if (!seen.Add(Normalize(java.Path))) continue;
            results.Add(java);
        }

        return results;
    }

    /// <summary>
    /// 只找**系统安装**的 Java（启动器自带的运行时不算）。
    ///
    /// 与 <see cref="Discover"/> 分开，是为了让界面能按
    /// "本地 → 系统默认位置 → 用户指定" 三档分别展示。
    /// </summary>
    public static IReadOnlyList<JavaInstallation> DiscoverSystem(int maxSystemCandidates = 12)
    {
        var results = new List<JavaInstallation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in EnumerateSystemRoots())
        {
            foreach (var javaExe in FindJavaExe(root, maxDepth: 4))
            {
                if (results.Count >= maxSystemCandidates) return results;

                // 同一条 java.exe 可能被多个根目录扫到（目录嵌套、8.3 短名等），
                // 统一用规范化后的真实路径去重
                if (!seen.Add(Normalize(javaExe))) continue;

                results.Add(new JavaInstallation(javaExe, ReadMajorVersion(javaExe) ?? 0, IsBundled: false, Component: null));
            }
        }

        return results;
    }

    /// <summary>把路径规范化成可比较的键：展开、去掉尾部分隔符、忽略大小写。</summary>
    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    /// <summary>广度优先、限深度地找 java.exe，避免扫爆整个 Program Files。</summary>
    private static IEnumerable<string> FindJavaExe(string root, int maxDepth)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(directory, "java.exe"); }
            catch { continue; }

            foreach (var file in files)
                if (string.Equals(Path.GetFileName(file), "java.exe", StringComparison.OrdinalIgnoreCase))
                    yield return file;

            if (depth >= maxDepth) continue;

            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch { continue; }

            foreach (var child in children) queue.Enqueue((child, depth + 1));
        }
    }
}
