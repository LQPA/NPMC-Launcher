using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using NPMC.Launcher.Core.Mods;

namespace NPMC.Launcher.Infrastructure.CurseForge;

/// <summary>CurseForge 的一个文件（ModFile）。</summary>
public sealed record CurseForgeFile(
    int ModId,
    int FileId,
    string FileName,
    string DisplayName,
    DateTimeOffset FileDate,
    long FileLength,
    string? DownloadUrl,
    int ReleaseType,
    IReadOnlyList<string> GameVersions,
    string? Sha1)
{
    /// <summary>
    /// 转换成与平台无关的候选版本。
    ///
    /// 注意：**即使下载地址为空也返回候选**。因为"当前已安装的这个文件"本身
    /// 不需要下载地址，而 UpdatePlanner 只会跳过缺少地址的*新版本*。
    /// CurseForge 的文件列表接口通常不带 downloadUrl，需要时再单独申请。
    /// </summary>
    public UpdateCandidate ToCandidate() => new()
    {
        Source = UpdateSource.CurseForge,
        ProjectId = ModId.ToString(CultureInfo.InvariantCulture),
        VersionLabel = string.IsNullOrWhiteSpace(DisplayName) ? FileName : DisplayName,
        PublishedAt = FileDate,
        FileName = FileName,
        DownloadUrl = DownloadUrl ?? "",
        Sha1 = Sha1,

        // CurseForge 的 releaseType: 1 = release, 2 = beta, 3 = alpha
        IsPrerelease = ReleaseType != 1,
        GameVersions = GameVersions,
    };
}

/// <summary>指纹与它对应的文件。</summary>
public sealed record CurseForgeMatch(uint Fingerprint, CurseForgeFile File);

/// <summary>
/// CurseForge 开放 API 的最小封装。
///
/// 与 Modrinth 的三点差异：
///   1. **必须带 API key**（请求头 x-api-key），没有 key 一律 403。
///   2. **识别文件用指纹**（MurmurHash2）而不是 SHA1。
///   3. **下载地址要单独申请**（/files/{id}/download-url），且部分作者禁止第三方下载，
///      此时该端点返回错误 —— 调用方需按"无法自动下载"处理，而不是当成失败。
///
/// Key 的来源（按优先级）：环境变量 CURSEFORGE_API_KEY → %APPDATA%\NPMC-Launcher\curseforge.key。
/// 绝不写入设置文件，也绝不打印到日志。
/// </summary>
public sealed class CurseForgeClient
{
    public const string ApiBase = "https://api.curseforge.com/v1";
    public const string ApiKeyEnvironmentVariable = "CURSEFORGE_API_KEY";

    /// <summary>CurseForge 的 ModLoaderType 枚举值。</summary>
    private static readonly Dictionary<string, int> LoaderTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forge"] = 1,
        ["fabric"] = 4,
        ["quilt"] = 5,
        ["neoforge"] = 6,
    };

    /// <summary>gameVersions 数组里加载器的显示名，用于客服端过滤。</summary>
    private static readonly Dictionary<string, string> LoaderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forge"] = "Forge",
        ["fabric"] = "Fabric",
        ["quilt"] = "Quilt",
        ["neoforge"] = "NeoForge",
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public CurseForgeClient(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    /// <summary>读取 API key；未配置或格式不合法时返回 null。</summary>
    public static string? ResolveApiKey() => TryResolveApiKey(out var apiKey, out _) ? apiKey : null;

    /// <summary>
    /// 读取并校验 API key。失败时通过 <paramref name="problem"/> 给出可操作的原因。
    ///
    /// 为什么需要校验：CurseForge 的 key 形如 <c>$2a$10$...</c>（bcrypt 格式）。
    /// 在 PowerShell 里用**双引号**设置时，<c>$</c> 后面的部分会被当成变量展开，
    /// key 于是被悄悄改坏；.NET 发请求时才抛
    /// "Request headers must contain only ASCII characters"，很难看出根因。
    /// 这里提前检查并给出"请用单引号"的提示。
    /// </summary>
    public static bool TryResolveApiKey(out string? apiKey, out string? problem)
    {
        apiKey = null;
        problem = null;

        var raw = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NPMC-Launcher",
                    "curseforge.key");

                if (File.Exists(path)) raw = File.ReadAllText(path);
            }
            catch
            {
                // 读不到就当未配置
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = $"未配置 {ApiKeyEnvironmentVariable}";
            return false;
        }

        var cleaned = Sanitize(raw);

        if (cleaned.Length == 0)
        {
            problem = "API key 为空";
            return false;
        }

        foreach (var character in cleaned)
        {
            if (character is >= (char)0x20 and <= (char)0x7E) continue;

            problem = $"API key 含有非 ASCII 字符（U+{(int)character:X4}）。"
                    + "多半是 PowerShell 双引号把 $ 开头的部分当成变量展开了 —— 请用单引号重新设置："
                    + " setx CURSEFORGE_API_KEY '$2a$10$...'";
            return false;
        }

        apiKey = cleaned;
        return true;
    }

    /// <summary>去掉 BOM、零宽字符与首尾空白/换行 —— 复制粘贴常带这些。</summary>
    private static string Sanitize(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            switch (character)
            {
                case '\uFEFF':   // BOM
                case '\u200B':   // 零宽空格
                case '\u200E':
                case '\u200F':
                    continue;
                default:
                    buffer.Append(character);
                    break;
            }
        }

        return buffer.ToString().Trim();
    }

    /// <summary>指纹查询结果：命中的文件 + 服务端报告未匹配的指纹。</summary>
    public sealed record FingerprintLookup(
        IReadOnlyList<CurseForgeMatch> Matches,
        IReadOnlyCollection<uint> Unmatched);

    /// <summary>按指纹批量识别文件。查不到的指纹不会出现在结果里。</summary>
    public async Task<IReadOnlyList<CurseForgeMatch>> GetMatchesAsync(
        IReadOnlyCollection<uint> fingerprints,
        CancellationToken cancellationToken = default)
        => (await LookupAsync(fingerprints, cancellationToken).ConfigureAwait(false)).Matches;

    /// <summary>
    /// 同上，但同时返回"未匹配"的指纹集合。
    /// 诊断用：一次把多种候选算法一起发过去，看哪个被命中。
    /// </summary>
    public async Task<FingerprintLookup> LookupAsync(
        IReadOnlyCollection<uint> fingerprints,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<CurseForgeMatch>();
        IReadOnlyCollection<uint> unmatched = Array.Empty<uint>();

        if (fingerprints.Count == 0) return new FingerprintLookup(matches, unmatched);

        using var document = await SendAsync(
            HttpMethod.Post,
            $"{ApiBase}/fingerprints",
            new { fingerprints = fingerprints.ToArray() },
            cancellationToken).ConfigureAwait(false);

        if (document is null) return new FingerprintLookup(matches, unmatched);
        if (!document.RootElement.TryGetProperty("data", out var data)) return new FingerprintLookup(matches, unmatched);

        if (data.TryGetProperty("exactMatches", out var exactMatches) && exactMatches.ValueKind == JsonValueKind.Array)
        {
            foreach (var match in exactMatches.EnumerateArray())
            {
                if (!match.TryGetProperty("file", out var fileElement)) continue;

                var file = ParseFile(fileElement);
                if (file is null) continue;

                var fingerprint = 0u;
                if (match.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
                    fingerprint = (uint)id;

                matches.Add(new CurseForgeMatch(fingerprint, file));
            }
        }

        if (data.TryGetProperty("unmatchedFingerprints", out var unmatchedElement) &&
            unmatchedElement.ValueKind == JsonValueKind.Array)
        {
            var values = new List<uint>();
            foreach (var item in unmatchedElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value))
                    values.Add((uint)value);
            }

            unmatched = values;
        }

        return new FingerprintLookup(matches, unmatched);
    }

    /// <summary>取某个项目在指定游戏版本/加载器下的文件，按发布时间从新到旧排序。</summary>
    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
        int modId,
        string gameVersion,
        IReadOnlyList<string> loaders,
        CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBase}/mods/{modId}/files";

        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += $"?gameVersion={Uri.EscapeDataString(gameVersion)}";

        using var document = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        if (document is null) return Array.Empty<CurseForgeFile>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<CurseForgeFile>();

        // 期望的加载器显示名；为空表示不按加载器过滤（原版/未知）
        var expectedLoaders = loaders
            .Where(LoaderDisplayNames.ContainsKey)
            .Select(loader => LoaderDisplayNames[loader])
            .ToList();

        var files = new List<CurseForgeFile>();

        foreach (var element in data.EnumerateArray())
        {
            var file = ParseFile(element);
            if (file is null) continue;

            if (expectedLoaders.Count > 0 && !MatchesAnyLoader(file.GameVersions, expectedLoaders)) continue;

            files.Add(file);
        }

        return files
            .OrderByDescending(file => file.FileDate)
            .ToList();
    }

    /// <summary>
    /// 申请下载地址。作者禁止第三方下载时 CurseForge 会返回错误，
    /// 这种情况返回 null（调用方据此提示"需前往网页下载"，而不是报错）。
    /// </summary>
    public async Task<string?> TryGetDownloadUrlAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await SendAsync(
                HttpMethod.Get,
                $"{ApiBase}/mods/{modId}/files/{fileId}/download-url",
                null,
                cancellationToken).ConfigureAwait(false);

            if (document is null) return null;
            if (!document.RootElement.TryGetProperty("data", out var data)) return null;

            return data.ValueKind == JsonValueKind.String ? data.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesAnyLoader(IReadOnlyList<string> gameVersions, IReadOnlyList<string> expected)
        => gameVersions.Any(version =>
            expected.Any(loader => string.Equals(version, loader, StringComparison.OrdinalIgnoreCase)));

    private async Task<JsonDocument?> SendAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static CurseForgeFile? ParseFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var modId = GetInt(element, "modId");
        var fileId = GetInt(element, "id");
        if (modId == 0 || fileId == 0) return null;

        var gameVersions = new List<string>();
        if (element.TryGetProperty("gameVersions", out var versionsElement) &&
            versionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var version in versionsElement.EnumerateArray())
            {
                if (version.ValueKind == JsonValueKind.String)
                    gameVersions.Add(version.GetString()!);
            }
        }

        return new CurseForgeFile(
            modId,
            fileId,
            GetString(element, "fileName") ?? "",
            GetString(element, "displayName") ?? "",
            ParseDate(GetString(element, "fileDate")),
            GetLong(element, "fileLength"),
            GetString(element, "downloadUrl"),
            GetInt(element, "releaseType"),
            gameVersions,
            GetSha1(element));
    }

    /// <summary>CurseForge 的 hashes 数组里 algo = 1 表示 SHA1。</summary>
    private static string? GetSha1(JsonElement element)
    {
        if (!element.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var hash in hashes.EnumerateArray())
        {
            if (GetInt(hash, "algo") != 1) continue;

            var value = GetString(hash, "value");
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static long GetLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static DateTimeOffset ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, out var parsed) ? parsed : default;

    /// <summary>加载器名 → CurseForge 的 ModLoaderType（供将来按类型过滤用）。</summary>
    public static int? ToModLoaderType(string loader) =>
        LoaderTypes.TryGetValue(loader, out var type) ? type : null;
}
