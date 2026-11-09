using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.Infrastructure.Translation;

/// <summary>
/// 机器翻译（把模组简介翻成用户的语言），带内存 + 磁盘缓存。
///
/// 用的是 Google 的公共 gtx 端点：**不需要 API key**，代价是它不属于正式 API
/// （没有 SLA、可能限流或失效）。因此这里的设计原则是"翻不出来就不显示"，
/// 绝不因为翻译失败而影响详情本身。
///
/// 缓存格式见 <see cref="TranslationCache"/>：一个可手改的 CSV（语言 + 原文 + 译文）
/// 加一个纯元数据 JSON。旧版「sha1.txt」会在命中时懒迁移进 CSV 后删除。
/// </summary>
public sealed class TranslationClient
{
    private const string GtxEndpoint = "https://translate.googleapis.com/translate_a/single";
    private const string DictEndpoint = "https://clients5.google.com/translate_a/t";
    private const string MyMemoryEndpoint = "https://api.mymemory.translated.net/get";

    /// <summary>单次翻译的字符上限。超长正文会先截断 —— 详情摘要本来就只显示前几百字。</summary>
    private const int MaxCharacters = 1800;

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TranslationCache _cache;
    private readonly Dictionary<string, string?> _memory = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;

    // 每个端点各自记冷却时间：实测 gtx 会持续 429，而 dict-chrome-ex / MyMemory 仍然可用。
    private DateTimeOffset _gtxCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _dictCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _myMemoryCooldownUntil = DateTimeOffset.MinValue;

    public TranslationClient(HttpClient http, string cacheDirectory, Action<string>? log = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _cache = new TranslationCache(cacheDirectory);
        _log = log;
    }

    /// <summary>三个端点是否都在冷却（诊断/界面提示用）。</summary>
    public bool IsCoolingDown =>
        DateTimeOffset.UtcNow < _gtxCooldownUntil
        && DateTimeOffset.UtcNow < _dictCooldownUntil
        && DateTimeOffset.UtcNow < _myMemoryCooldownUntil;

    /// <summary>缓存文件路径（诊断用）。</summary>
    public string CachePath => _cache.CsvPath;

    public async Task<string?> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (string.IsNullOrWhiteSpace(targetLanguage)) return null;

        var normalized = text.Length > MaxCharacters ? text[..MaxCharacters] : text;

        // key = sha1(语言|原文)：与旧版 .txt 文件名规则完全一致（那边是 Hash(lang|text)），
        // 所以历史缓存仍然能命中；CSV 的 key 列也因此是短哈希，而不是整段原文。
        var key = Hash($"{targetLanguage}|{normalized}");

        if (_memory.TryGetValue(key, out var cached)) return cached;

        // 1) 新格式 CSV
        if (_cache.TryGet(key, out var fromCsv))
        {
            _memory[key] = fromCsv;
            return fromCsv;
        }

        // 2) 旧版 <sha1>.txt：命中时补进 CSV 并删掉旧文件
        var legacyPath = Path.Combine(_cacheDirectory, key + ".txt");
        if (_cache.TryImportLegacy(legacyPath, key, targetLanguage, normalized, out var migrated))
        {
            _memory[key] = migrated;
            return migrated;
        }

        var result = await RequestAsync(normalized, targetLanguage, cancellationToken).ConfigureAwait(true);
        _memory[key] = result;

        if (result is not null)
        {
            _cache.Store(key, targetLanguage, normalized, result);
        }

        return result;
    }

    /// <summary>
    /// 依次尝试三个端点。公共翻译端点经常限流（gtx 实测 429），
    /// 单个端点失败不该让整个功能哑掉，所以这里做一条回退链；
    /// 每个端点各自记冷却时间，冷却期内直接跳过。
    /// </summary>
    private async Task<string?> RequestAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow >= _gtxCooldownUntil)
        {
            var viaGtx = await TryGtxAsync(text, targetLanguage, cancellationToken).ConfigureAwait(true);
            if (viaGtx is not null) return viaGtx;
        }

        if (DateTimeOffset.UtcNow >= _dictCooldownUntil)
        {
            var viaDict = await TryDictChromeAsync(text, targetLanguage, cancellationToken).ConfigureAwait(true);
            if (viaDict is not null) return viaDict;
        }

        if (DateTimeOffset.UtcNow >= _myMemoryCooldownUntil)
        {
            var viaMyMemory = await TryMyMemoryAsync(text, targetLanguage, cancellationToken).ConfigureAwait(true);
            if (viaMyMemory is not null) return viaMyMemory;
        }

        return null;
    }

    /// <summary>Google gtx（质量最好，但最容易被限流）。响应形如 [[["译文","原文",...], ...], null, "en"]。</summary>
    private async Task<string?> TryGtxAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        var url = $"{GtxEndpoint}?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}"
                  + $"&dt=t&q={Uri.EscapeDataString(text)}";

        var json = await GetJsonAsync(url, ct: cancellationToken, limitRef: () => _gtxCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10), endpointName: "gtx")
            .ConfigureAwait(true);

        if (json is null) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            var segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var builder = new StringBuilder();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;
                var piece = segment[0];
                if (piece.ValueKind == JsonValueKind.String) builder.Append(piece.GetString());
            }

            return Normalize(builder.ToString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Google 的 dict-chrome-ex 端点（gtx 被限流时通常还活着）。响应形如 [["译文","en"]]。</summary>
    private async Task<string?> TryDictChromeAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        var url = $"{DictEndpoint}?client=dict-chrome-ex&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}"
                  + $"&q={Uri.EscapeDataString(text)}";

        var json = await GetJsonAsync(url, ct: cancellationToken, limitRef: () => _dictCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10), endpointName: "dict-chrome-ex")
            .ConfigureAwait(true);

        if (json is null) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            // 两种形态：[["译文","en"]] 或 ["译文","en"]
            var builder = new StringBuilder();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    builder.Append(element.GetString());
                }
                else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0
                         && element[0].ValueKind == JsonValueKind.String)
                {
                    builder.Append(element[0].GetString());
                }
            }

            return Normalize(builder.ToString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// MyMemory 免费接口：匿名额度约 5000 字符/天，单次请求上限约 500 字符，
    /// 所以按句子切成不超过 450 字符的片段分别请求后拼接。作为最后兜底。
    /// </summary>
    private async Task<string?> TryMyMemoryAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        var parts = SplitForMyMemory(text, 450);
        if (parts.Count == 0) return null;

        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            // MyMemory 不接受 auto，必须显式给源语言：按字符集猜一个（正文多为英文）
            var url = $"{MyMemoryEndpoint}?q={Uri.EscapeDataString(part)}"
                      + $"&langpair={GuessSourceLanguage(part)}|{Uri.EscapeDataString(targetLanguage)}";

            var json = await GetJsonAsync(url, ct: cancellationToken, limitRef: () => _myMemoryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10), endpointName: "mymemory")
                .ConfigureAwait(true);

            if (json is null) return builder.Length > 0 ? Normalize(builder.ToString()) : null;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("responseData", out var data)
                    || !data.TryGetProperty("translatedText", out var translated)
                    || translated.ValueKind != JsonValueKind.String)
                {
                    return builder.Length > 0 ? Normalize(builder.ToString()) : null;
                }

                var piece = System.Net.WebUtility.HtmlDecode(translated.GetString() ?? string.Empty);

                // 额度和参数错误都以"译文"的形式返回，必须识别出来否则会把错误提示当译文存进缓存
                if (piece.Contains("INVALID SOURCE LANGUAGE", StringComparison.OrdinalIgnoreCase)
                    || piece.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
                    || piece.Contains("QUOTA", StringComparison.OrdinalIgnoreCase))
                {
                    _myMemoryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                    _log?.Invoke($"MyMemory 不可用：{piece[..Math.Min(80, piece.Length)]}");
                    return builder.Length > 0 ? Normalize(builder.ToString()) : null;
                }

                builder.Append(piece);
            }
            catch
            {
                return builder.Length > 0 ? Normalize(builder.ToString()) : null;
            }
        }

        return Normalize(builder.ToString());
    }

    /// <summary>GET 一个 JSON 文本；429/403 时调用方给的冷却回调生效并返回 null。</summary>
    private async Task<string?> GetJsonAsync(
        string url,
        CancellationToken ct,
        Action limitRef,
        string endpointName)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(true);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.Forbidden)
            {
                limitRef();
                _log?.Invoke($"翻译端点 {endpointName} 限流（{(int)response.StatusCode}），本轮改用其他端点");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"翻译端点 {endpointName} 返回 {(int)response.StatusCode}");
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"翻译端点 {endpointName} 失败：{ex.GetBaseException().Message}");
            return null;
        }
    }

    private static string? Normalize(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>按字符集猜源语言（MyMemory 必须显式给源语言，不接受 auto）。</summary>
    private static string GuessSourceLanguage(string text)
    {
        int latin = 0, kana = 0, hangul = 0, cyrillic = 0;

        foreach (var ch in text)
        {
            if (ch is >= '\u3040' and <= '\u30FF') kana++;
            else if (ch is >= '\uAC00' and <= '\uD7AF') hangul++;
            else if (ch is >= '\u0400' and <= '\u04FF') cyrillic++;
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }

        if (kana > 0) return "ja";
        if (hangul > 0) return "ko";
        if (cyrillic > latin) return "ru";
        return "en";
    }

    /// <summary>按句子/空格把长文本切成不超过 <paramref name="maxLength"/> 的片段。</summary>
    private static List<string> SplitForMyMemory(string text, int maxLength)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (var segment in text.Split(' ', '\n'))
        {
            if (current.Length + segment.Length + 1 > maxLength && current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0) current.Append(current.Length > 0 ? " " : string.Empty);

            if (segment.Length > maxLength)
            {
                for (var i = 0; i < segment.Length; i += maxLength)
                {
                    parts.Add(segment.Substring(i, Math.Min(maxLength, segment.Length - i)));
                }

                continue;
            }

            current.Append(segment);
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
    private static string Hash(string value)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
