using System.Net.Http.Json;
using System.Text.Json;
using NPMC.Launcher.Core.Mods;

namespace NPMC.Launcher.Infrastructure.Modrinth;

/// <summary>
/// Modrinth 开放 API 的最小封装。
///
/// 只需要两个端点：
///   POST /v2/version_files            —— 按文件哈希反查"这是哪个项目的哪个版本"（一次最多 1000 个）
///   GET  /v2/project/{id}/version     —— 取该项目的版本列表，按游戏版本与加载器过滤
///
/// 为什么必须过滤加载器：否则会给 Forge 整合包推 Fabric 版本的模组，下载下来加载不了。
/// 为什么不带 API key：这两个端点都是公开的，只要带上 User-Agent 就够
/// （Modrinth 用 UA 做限流归属，缺失会被拒）。
/// </summary>
public sealed class ModrinthClient
{
    public const string ApiBase = "https://api.modrinth.com/v2";
    public const int MaxHashesPerRequest = 1000;

    private readonly HttpClient _http;

    public ModrinthClient(HttpClient http) => _http = http;

    /// <summary>批量按 SHA1 反查版本；查不到的哈希不会出现在结果里。</summary>
    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
        IReadOnlyCollection<string> sha1Hashes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        if (sha1Hashes.Count == 0) return result;

        foreach (var batch in sha1Hashes.Chunk(MaxHashesPerRequest))
        {
            // Modrinth 要求**小写**十六进制；传大写不会报错，只会静默返回空结果。
            // FileHashing 产出大写（与 Mojang 的表示一致），所以在这个边界统一转小写。
            var normalized = batch.Select(hash => hash.ToLowerInvariant()).ToArray();
            var body = new { hashes = normalized, algorithm = "sha1" };

            using var response = await _http
                .PostAsJsonAsync($"{ApiBase}/version_files", body, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 响应是 { "<sha1>": <version>, ... }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var version = ParseVersion(property.Value);
                if (version is not null) result[property.Name] = version;
            }
        }

        return result;
    }

    /// <summary>取某个项目匹配指定游戏版本与加载器的全部版本。</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetProjectVersionsAsync(
        string projectId,
        string? gameVersion,
        IReadOnlyList<string> loaders,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameVersion))
            parameters.Add($"game_versions=[\"{Uri.EscapeDataString(gameVersion)}\"]");

        if (loaders.Count > 0)
        {
            var list = string.Join(",", loaders.Select(loader => $"\"{Uri.EscapeDataString(loader)}\""));
            parameters.Add($"loaders=[{list}]");
        }

        var url = $"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version";
        if (parameters.Count > 0) url += "?" + string.Join("&", parameters);

        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        var versions = new List<ModrinthVersion>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return versions;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var version = ParseVersion(element);
            if (version is not null) versions.Add(version);
        }

        return versions;
    }

    /// <summary>搜索项目（按相关性）。facets 会把项目类型、游戏版本与加载器都限死。</summary>
    public async Task<IReadOnlyList<ModrinthSearchHit>> SearchAsync(
        string query,
        string? gameVersion,
        IReadOnlyList<string> loaders,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var facets = new List<string> { "[\"project_type:mod\"]" };

        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add($"[\"versions:{gameVersion}\"]");

        if (loaders.Count > 0)
            facets.Add($"[{string.Join(",", loaders.Select(loader => $"\"categories:{loader}\""))}]");

        var url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}"
                + $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}"
                + $"&limit={limit}"
                + "&index=relevance";

        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        var hits = new List<ModrinthSearchHit>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("hits", out var hitsElement) ||
            hitsElement.ValueKind != JsonValueKind.Array)
            return hits;

        foreach (var hit in hitsElement.EnumerateArray())
        {
            var projectId = GetString(hit, "project_id");
            if (projectId is null) continue;

            hits.Add(new ModrinthSearchHit(
                projectId,
                GetString(hit, "slug") ?? "",
                GetString(hit, "title") ?? "",
                GetString(hit, "description") ?? "",
                GetString(hit, "icon_url") ?? "",
                GetLong(hit, "downloads"),
                GetStringArray(hit, "categories")));
        }

        return hits;
    }

    /// <summary>批量取项目信息（用于把 projectId 变成人看得懂的名字）。</summary>
    public async Task<IReadOnlyDictionary<string, ModrinthProject>> GetProjectsAsync(
        IReadOnlyCollection<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModrinthProject>(StringComparer.Ordinal);
        if (projectIds.Count == 0) return result;

        foreach (var batch in projectIds.Distinct().Chunk(100))
        {
            var ids = "[" + string.Join(",", batch.Select(id => $"\"{Uri.EscapeDataString(id)}\"")) + "]";
            var url = $"{ApiBase}/projects?ids={Uri.EscapeDataString(ids)}";

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) continue;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = GetString(element, "id");
                if (id is null) continue;

                result[id] = new ModrinthProject(
                    id,
                    GetString(element, "slug") ?? "",
                    GetString(element, "title") ?? "",
                    GetString(element, "description") ?? "");
            }
        }

        return result;
    }

    /// <summary>取单个版本（依赖里钉死了 version_id 时用）。</summary>
    public async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var json = await _http
            .GetStringAsync($"{ApiBase}/version/{Uri.EscapeDataString(versionId)}", cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        return ParseVersion(document.RootElement);
    }

    private static ModrinthVersion? ParseVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var id = GetString(element, "id");
        var projectId = GetString(element, "project_id");
        if (id is null || projectId is null) return null;

        var files = new List<ModrinthFile>();

        if (element.TryGetProperty("files", out var filesElement) &&
            filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                var url = GetString(file, "url");
                if (url is null) continue;

                files.Add(new ModrinthFile(
                    GetString(file, "filename") ?? "",
                    url,
                    GetNestedString(file, "hashes", "sha1") ?? "",
                    GetLong(file, "size"),
                    file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True));
            }
        }

        return new ModrinthVersion(
            id,
            projectId,
            GetString(element, "version_number") ?? "",
            GetString(element, "name") ?? "",
            GetString(element, "version_type") ?? "",
            ParseDate(GetString(element, "date_published")),
            GetStringArray(element, "game_versions"),
            GetStringArray(element, "loaders"),
            files)
        {
            Dependencies = ParseDependencies(element),
        };
    }

    /// <summary>解析 dependencies 数组（required / optional / incompatible / embedded）。</summary>
    private static IReadOnlyList<ModrinthDependency> ParseDependencies(JsonElement element)
    {
        if (!element.TryGetProperty("dependencies", out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<ModrinthDependency>();

        var dependencies = new List<ModrinthDependency>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var type = GetString(item, "dependency_type");
            if (string.IsNullOrWhiteSpace(type)) continue;

            dependencies.Add(new ModrinthDependency(
                GetString(item, "version_id"),
                GetString(item, "project_id"),
                type));
        }

        return dependencies;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string property, string nested)
    {
        if (!element.TryGetProperty(property, out var container)) return null;
        if (container.ValueKind != JsonValueKind.Object) return null;
        return GetString(container, nested);
    }

    private static long GetLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static DateTimeOffset ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, out var parsed) ? parsed : default;
}
