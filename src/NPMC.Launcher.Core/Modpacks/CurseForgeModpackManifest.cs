using System.Text.Json;

namespace NPMC.Launcher.Core.Modpacks;

/// <summary>CF 整合包 manifest 里的一个文件：只有 projectID / fileID，没有文件名和地址。</summary>
public sealed record CurseForgeModpackFile(int ProjectId, int FileId, bool Required);

/// <summary>
/// CurseForge 整合包的 <c>manifest.json</c>。
///
/// 与 Modrinth 的 `.mrpack` 不同：这里没有下载地址、没有 SHA1，甚至没有文件名；
/// 每个文件都要用 (projectID, fileID) 去 CurseForge API 换 URL。
/// </summary>
public sealed record CurseForgeModpackManifest(
    string Name,
    string Version,
    string GameVersion,
    string? LoaderName,
    string? LoaderVersion,
    IReadOnlyList<CurseForgeModpackFile> Files,
    string OverridesDirectory,
    int RecommendedRam);

public static class CurseForgeModpackManifestParser
{
    /// <summary>
    /// 解析 manifest.json。加载器 id 形如 <c>neoforge-21.1.259</c> / <c>forge-47.2.0</c> /
    /// <c>fabric-0.15.11</c>，按第一个 '-' 拆成名字与版本。
    /// </summary>
    public static CurseForgeModpackManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("CF manifest.json 不是 JSON 对象");

        var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "整合包" : "整合包";
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? "" : "";

        var gameVersion = "";
        string? loaderName = null;
        string? loaderVersion = null;
        var recommendedRam = 0;

        if (root.TryGetProperty("minecraft", out var minecraft) && minecraft.ValueKind == JsonValueKind.Object)
        {
            if (minecraft.TryGetProperty("version", out var gameElement))
                gameVersion = gameElement.GetString() ?? "";

            if (minecraft.TryGetProperty("recommendedRam", out var ramElement)
                && ramElement.ValueKind == JsonValueKind.Number)
            {
                recommendedRam = ramElement.GetInt32();
            }

            if (minecraft.TryGetProperty("modLoaders", out var loaders)
                && loaders.ValueKind == JsonValueKind.Array)
            {
                foreach (var loader in loaders.EnumerateArray())
                {
                    var id = loader.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var dash = id.IndexOf('-');
                    if (dash <= 0) continue;

                    loaderName = id[..dash];
                    loaderVersion = id[(dash + 1)..];
                    break;
                }
            }
        }

        var files = new List<CurseForgeModpackFile>();

        if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                if (!file.TryGetProperty("projectID", out var projectElement)
                    || !file.TryGetProperty("fileID", out var fileIdElement))
                {
                    continue;
                }

                var required = !file.TryGetProperty("required", out var requiredElement)
                               || requiredElement.ValueKind != JsonValueKind.False;

                files.Add(new CurseForgeModpackFile(
                    projectElement.GetInt32(), fileIdElement.GetInt32(), required));
            }
        }

        var overrides = root.TryGetProperty("overrides", out var overridesElement)
            ? overridesElement.GetString() ?? "overrides"
            : "overrides";

        return new CurseForgeModpackManifest(
            name, version, gameVersion, loaderName, loaderVersion, files, overrides, recommendedRam);
    }
}
