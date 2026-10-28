namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 算「整合包自带文件」清单。
///
/// 为什么不能只看 `modrinth.index.json` 的 files[]：整合包会把一部分模组直接放进
/// `overrides/`（不需要联网下载的那些），安装后它们就是普通 jar，如果不在清单里，
/// 界面会当成"用户自己加的模组"，不会折叠也不带整合包标记。
/// 所以安装完再扫一遍 mods 目录：凡是清单之外、确实存在的 jar 都算整合包自带。
/// </summary>
public static class PackFileCollector
{
    /// <summary>禁用文件的后缀（`x.jar.disabled` 的逻辑名仍是 `x.jar`）。</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>会被当作"整合包内容"扫描的目录与扩展名（模组 / 资源包 / 光影）。</summary>
    private static readonly (string Directory, string[] Patterns)[] ContentDirectories =
    {
        ("mods", new[] { ".jar", ".jar" + DisabledSuffix }),
        ("resourcepacks", new[] { ".zip", ".zip" + DisabledSuffix }),
        ("shaderpacks", new[] { ".zip", ".zip" + DisabledSuffix }),
    };

    /// <summary>把实际文件名还原成逻辑名：去掉 `.disabled`。</summary>
    public static string LogicalName(string fileName) =>
        fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^DisabledSuffix.Length]
            : fileName;

    /// <summary>
    /// 合并"清单里的文件"与实例目录里实际存在的内容文件，按逻辑文件名去重。
    ///
    /// 清单里的条目保持 <see cref="PackFileEntry.FromIndex"/> = true；
    /// 目录里多出来的（来自 `overrides/`）记成 false —— 界面据此决定是否打来源徽标。
    /// </summary>
    public static IReadOnlyList<PackFileEntry> Collect(
        IEnumerable<PackFileEntry> indexFiles,
        string gameDirectory)
    {
        var result = new List<PackFileEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in indexFiles)
        {
            var logical = LogicalName(entry.FileName);
            if (!names.Add(logical)) continue;

            result.Add(entry with { FileName = logical });
        }

        foreach (var (directory, patterns) in ContentDirectories)
        {
            var root = Path.Combine(gameDirectory, directory);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);

                if (!patterns.Any(pattern => name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var logical = LogicalName(name);
                if (!names.Add(logical)) continue;

                // 目录里扫到、清单里没有的：来自 overrides/（自制或三方内容），
                // 平台不认识它们，徽标要保持 `?`
                result.Add(new PackFileEntry(logical) { FromIndex = false });
            }
        }

        return result;
    }
}
