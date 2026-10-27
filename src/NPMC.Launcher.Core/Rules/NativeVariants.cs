using NPMC.Launcher.Core.Models;

namespace NPMC.Launcher.Core.Rules;

/// <summary>
/// 原生库（natives）的架构选择。
///
/// 为什么需要它：版本 JSON 里同一个库可能同时声明多个 natives 变体，而**旧写法只按
/// 操作系统过滤**。26.3 的官方元数据就是这样：
///
///   org.lwjgl:lwjgl:3.4.3:natives-windows        rules=[{"allow","os":{"name":"windows"}}]
///   org.lwjgl:lwjgl:3.4.3:natives-windows-arm64  rules=[{"allow","os":{"name":"windows"}}]
///
/// 两条都命中 → 两个 jar 一起进 classpath。LWJGL 在 classpath 里看到的是 arm64 的本机库，
/// 于是报 "Platform/architecture mismatch ... Platform available on classpath: windows/arm64"，
/// 紧接着 FML 的早期窗口连带崩掉（NullPointerException in DisplayWindow）。
///
/// 官方启动器的做法是按架构只挑一个；这里用同样的策略：
///   - 同一个 group:artifact:version 只保留一个 natives 变体；
///   - 当前是 arm64 就挑带 arm64 的那个，否则挑不带 arm64 的那个；
///   - 只有一种可选时就用它（宁可保留也不要凭空丢库）。
/// 非 natives 的库原样返回，顺序不变。
/// </summary>
public static class NativeVariants
{
    private const string NativesPrefix = "natives-";

    /// <summary>按当前架构，把同库的多个 natives 变体收敛成一个。</summary>
    public static IReadOnlyList<Library> SelectForArchitecture(IReadOnlyList<Library> libraries, OsInfo os)
    {
        var isArm64 = os.Arch.Contains("arm64", StringComparison.OrdinalIgnoreCase);

        // 每个 group:artifact:version 只留一个 natives 变体。
        // null 值表示"这一组里出现过的变体"，用于决定留谁。
        var chosen = new Dictionary<string, Library>(StringComparer.Ordinal);
        var result = new List<Library>(libraries.Count);

        foreach (var library in libraries)
        {
            if (!TryGetNativesKey(library, out var key, out var wantsArm64))
            {
                result.Add(library);
                continue;
            }

            if (!chosen.TryGetValue(key, out var existing))
            {
                chosen[key] = library;
                result.Add(library);
                continue;
            }

            // 已经有一个了：只有"当前这个更符合架构"才替换
            var existingArm64 = ClassifierWantsArm64(existing);
            var shouldReplace = wantsArm64 == isArm64 && existingArm64 != isArm64;

            if (!shouldReplace) continue;

            var index = result.IndexOf(existing);
            if (index >= 0) result[index] = library;
            chosen[key] = library;
        }

        return result;
    }

    /// <summary>是不是 natives 变体；是的话给出所属库的键，以及它是否面向 arm64。</summary>
    private static bool TryGetNativesKey(Library library, out string key, out bool wantsArm64)
    {
        key = "";
        wantsArm64 = false;

        var parts = library.Name.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;   // 没有 classifier，不是 natives 变体

        var classifier = parts[3];
        if (!classifier.StartsWith(NativesPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        key = $"{parts[0]}:{parts[1]}:{parts[2]}";
        wantsArm64 = classifier.Contains("arm64", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool ClassifierWantsArm64(Library library)
    {
        var parts = library.Name.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4
               && parts[3].Contains("arm64", StringComparison.OrdinalIgnoreCase);
    }
}
