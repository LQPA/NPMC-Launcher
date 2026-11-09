namespace NPMC.Launcher.Core.Modpacks;

/// <summary>
/// 合并导出时的 JVM 参数：实例自带的 + 作者手工追加的。
///
/// 规则（不做阻塞式校验，只在合并结果里保持"后者生效"的直觉）：
///  - 完全相同的参数只保留一个；
///  - 同类参数（<c>-Xmx</c> / <c>-Xms</c>）后者覆盖前者（追加的写在后面 → 以追加的为准）；
///  - 其它参数原样保留顺序。
/// </summary>
public static class JvmArgumentMerger
{
    /// <summary>同键覆盖的参数前缀（不带值的键也在这里）。</summary>
    private static readonly string[] SingleValuePrefixes = { "-Xmx", "-Xms", "-Xss", "-XX:MaxHeapSize" };

    public sealed record Result(
        IReadOnlyList<string> Arguments,
        int DuplicatesRemoved,
        int Overridden,
        IReadOnlyList<string> Notes);

    public static Result Merge(IEnumerable<string> instanceArguments, IEnumerable<string> extraArguments)
    {
        var combined = new List<string>();
        var notes = new List<string>();
        var duplicates = 0;
        var overridden = 0;

        void Add(string raw)
        {
            var argument = raw.Trim();
            if (argument.Length == 0) return;

            if (combined.Contains(argument, StringComparer.Ordinal))
            {
                duplicates++;
                return;
            }

            var single = SingleValuePrefixes.FirstOrDefault(prefix =>
                argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && argument.Length > prefix.Length);

            if (single is not null)
            {
                // 同类参数：把前面的删掉，后者生效
                var existing = combined.FindIndex(item =>
                    item.StartsWith(single, StringComparison.OrdinalIgnoreCase) && item.Length > single.Length);

                if (existing >= 0)
                {
                    notes.Add($"{combined[existing]} 被 {argument} 覆盖");
                    combined.RemoveAt(existing);
                    overridden++;
                }
            }

            combined.Add(argument);
        }

        foreach (var argument in instanceArguments) Add(argument);
        foreach (var argument in extraArguments) Add(argument);

        return new Result(combined, duplicates, overridden, notes);
    }

    /// <summary>把一行文本切成参数（支持双引号包裹）。</summary>
    public static List<string> Parse(string? text) => ModpackExtras.SplitArguments(text ?? string.Empty);
}
