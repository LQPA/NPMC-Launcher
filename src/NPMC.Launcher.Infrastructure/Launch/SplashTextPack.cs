using System.IO.Compression;
using System.Text;

namespace NPMC.Launcher.Infrastructure.Launch;

/// <summary>
/// 实例「高级选项 → 自定义横幅」：改主界面标题右侧的黄色标语（splash text）。
///
/// 实现方式：生成一个极小的资源包覆盖 <c>assets/minecraft/texts/splashes.txt</c>，
/// 并在 options.txt 里把它加进 resourcePacks。两条路径都验证过：
///   - 1.7.10：GuiMainMenu 通过 ResourceManager 读 splashes.txt，资源包可覆盖；
///   - 1.21.1：SplashManager 通过 ResourceManager.openAsReader 读，同样可覆盖。
///
/// 两个版本时代的差异只在 options.txt 里的写法：
///   - 1.13+ 是 <c>file/nmpc-splash.zip</c>；
///   - 1.12.2 及以前是裸文件名 <c>nmpc-splash.zip</c>。
///
/// 不启用时会把资源包和 options.txt 里的条目一起清掉，不留垃圾。
/// </summary>
public static class SplashTextPack
{
    public const string PackFileName = "nmpc-splash.zip";

    private const string ModernEntry = "file/" + PackFileName;
    private const string LegacyEntry = PackFileName;

    private const string PackMetaFormat =
        "{{\"pack\":{{\"pack_format\":{0},\"description\":\"NPMC 自定义横幅\","
        + "\"supported_formats\":{{\"min_inclusive\":1,\"max_inclusive\":999}}}}}}";

    /// <summary>应用/清除自定义横幅。失败时静默（不能影响启动）。</summary>
    public static void Apply(string gameDirectory, string minecraftVersion, bool modern, string? splashText)
    {
        try
        {
            var resourcePacks = Path.Combine(gameDirectory, "resourcepacks");
            var packPath = Path.Combine(resourcePacks, PackFileName);
            var optionsPath = Path.Combine(gameDirectory, "options.txt");

            if (string.IsNullOrWhiteSpace(splashText))
            {
                if (!File.Exists(packPath) && !HasEntry(optionsPath, modern)) return;

                TryDelete(packPath);
                WriteOptions(optionsPath, modern, enable: false);
                return;
            }

            Directory.CreateDirectory(resourcePacks);
            WritePack(packPath, minecraftVersion, modern, splashText.Trim());
            WriteOptions(optionsPath, modern, enable: true);
        }
        catch
        {
            // 高级选项失败不影响启动
        }
    }

    /// <summary>resouce pack format：<see cref="RewriteOptions"/> 与测试用。</summary>
    public static int PackFormat(string minecraftVersion, bool modern)
    {
        if (!Version.TryParse(NormalizeVersion(minecraftVersion), out var version))
            return modern ? 34 : 1;

        if (version < new Version(1, 9)) return 1;
        if (version < new Version(1, 11)) return 2;
        if (version < new Version(1, 13)) return 3;
        if (version < new Version(1, 15)) return 4;
        if (version < new Version(1, 16, 2)) return 5;
        if (version < new Version(1, 17)) return 6;
        if (version < new Version(1, 18)) return 7;
        if (version < new Version(1, 19)) return 8;
        if (version < new Version(1, 19, 3)) return 9;
        if (version < new Version(1, 19, 4)) return 12;
        if (version < new Version(1, 20)) return 13;
        if (version < new Version(1, 20, 2)) return 15;
        if (version < new Version(1, 20, 3)) return 18;
        if (version < new Version(1, 20, 5)) return 22;
        if (version < new Version(1, 21)) return 32;
        if (version < new Version(1, 21, 2)) return 34;
        if (version < new Version(1, 21, 4)) return 42;
        if (version < new Version(1, 21, 5)) return 46;
        if (version < new Version(1, 21, 6)) return 55;
        if (version < new Version(1, 21, 7)) return 63;
        if (version < new Version(1, 21, 9)) return 64;
        if (version < new Version(1, 22)) return 69;

        // 26.x 之后版本号不再是 1.x：pack_format 只是兜底，兼容性靠 supported_formats
        return 69;
    }

    /// <summary>
    /// 把 resourcePacks / incompatibleResourcePacks 两行改好。
    /// 抽成纯函数方便测试：输入原文件内容（或 null），输出新内容。
    /// </summary>
    public static string RewriteOptions(string? existing, bool modern, bool enable)
    {
        var entry = modern ? ModernEntry : LegacyEntry;
        var text = existing ?? "";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var resourceIndex = lines.FindIndex(line => line.StartsWith("resourcePacks:", StringComparison.Ordinal));
        var resourceLine = resourceIndex >= 0
            ? lines[resourceIndex]
            : "resourcePacks:" + (modern ? "[\"vanilla\"]" : "[]");

        if (enable || GetEntries(resourceLine).Contains(entry, StringComparer.Ordinal))
        {
            resourceLine = SetEntry(resourceLine, entry, enable);

            if (resourceIndex >= 0) lines[resourceIndex] = resourceLine;
            else if (enable) lines.Add(resourceLine);
        }

        var incompatibleIndex = lines.FindIndex(line =>
            line.StartsWith("incompatibleResourcePacks:", StringComparison.Ordinal));

        if (incompatibleIndex >= 0)
            lines[incompatibleIndex] = SetEntry(lines[incompatibleIndex], entry, enable: false);

        // 去掉末尾多余的空行（原文件通常以换行结尾，Split 后最后一个元素是空串）
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        return string.Join(newline, lines) + newline;
    }

    private static bool HasEntry(string optionsPath, bool modern)
    {
        try
        {
            if (!File.Exists(optionsPath)) return false;

            var entry = modern ? ModernEntry : LegacyEntry;
            return File.ReadAllLines(optionsPath)
                .Where(line => line.StartsWith("resourcePacks:", StringComparison.Ordinal))
                .Any(line => GetEntries(line).Contains(entry, StringComparer.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static void WriteOptions(string optionsPath, bool modern, bool enable)
    {
        string? existing = null;

        try
        {
            if (File.Exists(optionsPath)) existing = File.ReadAllText(optionsPath);
        }
        catch
        {
            // 读不到就当空文件
        }

        var updated = RewriteOptions(existing, modern, enable);

        if (string.Equals(existing, updated, StringComparison.Ordinal)) return;

        File.WriteAllText(optionsPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WritePack(string packPath, string minecraftVersion, bool modern, string splashText)
    {
        if (File.Exists(packPath)) TryDelete(packPath);

        using var archive = ZipFile.Open(packPath, ZipArchiveMode.Create);

        WriteEntry(archive, "pack.mcmeta",
            string.Format(PackMetaFormat, PackFormat(minecraftVersion, modern)));

        WriteEntry(archive, "assets/minecraft/texts/splashes.txt", splashText + "\n");
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static List<string> GetEntries(string line)
    {
        var start = line.IndexOf('[');
        var end = line.LastIndexOf(']');
        if (start < 0 || end <= start) return new List<string>();

        return line[(start + 1)..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().Trim('"'))
            .Where(item => item.Length > 0)
            .ToList();
    }

    private static string SetEntry(string line, string entry, bool enable)
    {
        var values = GetEntries(line);
        values.RemoveAll(value => string.Equals(value, entry, StringComparison.Ordinal));
        if (enable) values.Add(entry);

        var prefix = line.StartsWith("incompatibleResourcePacks:", StringComparison.Ordinal)
            ? "incompatibleResourcePacks:"
            : "resourcePacks:";

        return prefix + "[" + string.Join(",", values.Select(value => "\"" + value + "\"")) + "]";
    }

    private static string NormalizeVersion(string version)
    {
        var dash = version.IndexOf('-');
        return dash > 0 ? version[..dash] : version;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉不影响启动
        }
    }
}
