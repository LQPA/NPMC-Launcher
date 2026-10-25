using System.Text.Json;

namespace NPMC.Launcher.Core.Modpacks;

/// <summary>整合包里的一个文件（路径相对于实例目录）。</summary>
public sealed record ModpackFile(
    string Path,
    string Sha1,
    long FileSize,
    IReadOnlyList<string> Downloads)
{
    /// <summary>是否落在 mods 目录下（用于统计，不影响安装）。</summary>
    public bool IsMod => Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// `.mrpack` 里的 <c>modrinth.index.json</c>。
///
/// 关键点：整合包**不自带模组文件**，它给的是"下载清单"——
/// 每个文件的相对路径、SHA1 与下载地址，再加上 <c>overrides/</c> 里的配置与资源。
/// 所以安装整合包 = 解压覆盖层 + 按清单逐个下载。
/// </summary>
public sealed record ModpackIndex(
    string Name,
    string VersionId,
    string GameVersion,
    string? LoaderName,
    string? LoaderVersion,
    IReadOnlyList<ModpackFile> Files)
{
    /// <summary>是否为 Fabric 系整合包（目前只有这一种能自动装加载器）。</summary>
    public bool IsFabric => string.Equals(LoaderName, "fabric-loader", StringComparison.OrdinalIgnoreCase);

    public int ModCount => Files.Count(file => file.IsMod);

    public long TotalBytes => Files.Sum(file => file.FileSize);

    public string LoaderText => LoaderName is null
        ? "原版"
        : $"{LoaderName} {LoaderVersion}";
}

public static class ModpackIndexParser
{
    /// <summary>解析 <c>modrinth.index.json</c> 的内容。</summary>
    public static ModpackIndex Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("modrinth.index.json 不是 JSON 对象");

        // 依赖表里同时包含游戏版本与加载器版本
        var gameVersion = "";
        string? loaderName = null;
        string? loaderVersion = null;

        if (root.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
        {
            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.String) continue;

                var value = dependency.Value.GetString() ?? "";

                if (string.Equals(dependency.Name, "minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    gameVersion = value;
                    continue;
                }

                // forge / neoforge / fabric-loader / quilt-loader
                if (dependency.Name.Contains("loader", StringComparison.OrdinalIgnoreCase)
                    || dependency.Name.Contains("forge", StringComparison.OrdinalIgnoreCase))
                {
                    loaderName = dependency.Name;
                    loaderVersion = value;
                }
            }
        }

        var files = new List<ModpackFile>();

        if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;

                var path = GetString(file, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;

                var downloads = new List<string>();
                if (file.TryGetProperty("downloads", out var urls) && urls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var url in urls.EnumerateArray())
                        if (url.ValueKind == JsonValueKind.String) downloads.Add(url.GetString()!);
                }

                // 没有任何下载地址就无法安装，跳过
                if (downloads.Count == 0) continue;

                var sha1 = "";
                if (file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                    sha1 = GetString(hashes, "sha1") ?? "";

                files.Add(new ModpackFile(
                    NormalizePath(path!),
                    sha1,
                    file.TryGetProperty("fileSize", out var size) && size.ValueKind == JsonValueKind.Number
                        ? size.GetInt64()
                        : 0,
                    downloads));
            }
        }

        var name = GetString(root, "name") ?? "";

        return new ModpackIndex(
            string.IsNullOrWhiteSpace(name) ? "整合包" : name!,
            GetString(root, "versionId") ?? "",
            gameVersion,
            loaderName,
            loaderVersion,
            files);
    }

    /// <summary>路径统一成正斜杠，并拒绝越界（zip 条目可能带 ..\）。</summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');

        if (normalized.Split('/').Any(segment => segment == ".."))
            throw new InvalidDataException($"整合包条目路径越界：{path}");

        return normalized;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
