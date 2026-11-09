using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>导出 MultiMC 实例包时需要的参数。</summary>
public sealed record MultiMcExportRequest(
    string InstanceDirectory,
    string TargetPath,
    string Name,
    string GameVersion,
    string? LoaderKey,
    string? LoaderVersion,
    int MemoryMb,
    int WindowWidth,
    int WindowHeight,
    bool IncludeSaves,
    string? Notes = null,
    bool IncludeMods = true,
    bool IncludeResourcePacks = true,
    bool IncludeShaderPacks = true);

/// <summary>导出结果。</summary>
public sealed record MultiMcExportResult(int Files, long Bytes, IReadOnlyList<string> Skipped);

/// <summary>
/// 导出成 MultiMC 实例包：
///
/// <code>
/// mmc-pack.json     组件清单（net.minecraft + 加载器 uid/version）
/// instance.cfg      名字 / 内存 / 窗口尺寸 / 备注
/// .packignore       空文件（MultiMC 认得，表示没有额外忽略规则）
/// .minecraft/…      实例文件（模组、配置、资源包…，排除 logs / crash-reports）
/// </code>
///
/// 为什么用它替代原来的「完整 ZIP」：完整 ZIP 只有我们自己能生成、**谁也导不回来**；
/// 而这个格式能按清单重建版本与加载器环境，导出后可以再导入，形成闭环。
/// </summary>
public static class MultiMcPackExporter
{
    /// <summary>打不进包的目录（日志永远是垃圾数据；saves 默认也不带，见 IncludeSaves）。</summary>
    private static readonly string[] AlwaysSkipped = { "logs", "crash-reports" };

    /// <summary>我们自己的标记文件，不属于游戏内容。</summary>
    private static readonly string[] InternalMarkers = { ".nmpc-instance.json", ".nmpc-modpack.json", "instance.nmpc.json" };

    public static MultiMcExportResult Export(
        MultiMcExportRequest request,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<(string Source, string Relative)>();
        var skipped = new List<string>();

        foreach (var file in Directory.EnumerateFiles(request.InstanceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(request.InstanceDirectory, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, '/');

            if (parts.Any(part => AlwaysSkipped.Contains(part, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!request.IncludeSaves
                && parts.Any(part => string.Equals(part, "saves", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // 导出页的「导出内容」勾选对 MultiMC 包同样生效
            if (!request.IncludeMods
                && parts.Contains("mods", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.IncludeResourcePacks
                && parts.Contains("resourcepacks", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.IncludeShaderPacks
                && parts.Contains("shaderpacks", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts.Length == 1
                && InternalMarkers.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add((file, relative));
        }

        long bytes = 0;
        var written = 0;

        using (var archive = ZipFile.Open(request.TargetPath, ZipArchiveMode.Create))
        {
            foreach (var (source, relative) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(".minecraft/" + relative.Replace('\\', '/'),
                    CompressionLevel.Optimal);

                using (var input = File.OpenRead(source))
                using (var output = entry.Open())
                {
                    input.CopyTo(output);
                }

                bytes += new FileInfo(source).Length;
                written++;
                progress?.Invoke(written, files.Count, relative);
            }

            WriteText(archive, "mmc-pack.json", BuildPackJson(request), indented: true);
            WriteText(archive, "instance.cfg", BuildInstanceCfg(request), indented: false);
            WriteText(archive, ".packignore", string.Empty, indented: false);
        }

        return new MultiMcExportResult(written, bytes, skipped);
    }

    /// <summary>加载器 key（LoaderDetector 的返回值）→ MultiMC 组件 uid。</summary>
    public static string? MapLoaderUid(string? loaderKey) => loaderKey?.ToLowerInvariant() switch
    {
        "fabric-loader" or "fabric" => "net.fabricmc.fabric-loader",
        "quilt-loader" or "quilt" => "org.quiltmc.quilt-loader",
        "neoforge" => "net.neoforged",
        "forge" => "net.minecraftforge",
        _ => null,
    };

    /// <summary>构造 mmc-pack.json。没有加载器时只写 net.minecraft（原版实例）。</summary>
    public static string BuildPackJson(MultiMcExportRequest request)
    {
        var components = new List<object>
        {
            new { important = true, dependencyOnly = false, uid = "net.minecraft", version = request.GameVersion },
        };

        var loaderUid = MapLoaderUid(request.LoaderKey);
        if (loaderUid is not null && !string.IsNullOrWhiteSpace(request.LoaderVersion))
        {
            components.Add(new
            {
                important = false,
                dependencyOnly = false,
                uid = loaderUid,
                version = request.LoaderVersion,
            });
        }

        return JsonSerializer.Serialize(new { formatVersion = 1, components }, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    /// <summary>构造 instance.cfg（MultiMC 的 key=value 格式）。</summary>
    public static string BuildInstanceCfg(MultiMcExportRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#Auto generated by NPMC Launcher");
        builder.AppendLine();
        builder.AppendLine("InstanceType=OneSix");
        builder.AppendLine($"name={request.Name.Replace('\n', ' ')}");
        builder.AppendLine($"MaxMemAlloc={Math.Max(512, request.MemoryMb)}");
        builder.AppendLine($"MinecraftWinWidth={Math.Max(320, request.WindowWidth)}");
        builder.AppendLine($"MinecraftWinHeight={Math.Max(240, request.WindowHeight)}");

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            // MultiMC 的 notes 是单行，换行用字面量 \n 表示
            builder.AppendLine("notes=" + request.Notes.Replace("\r", string.Empty).Replace("\n", "\\n"));
        }

        return builder.ToString();
    }

    private static void WriteText(ZipArchive archive, string entryName, string content, bool indented)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(indented));
        writer.Write(content);
    }
}
