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
    private const string BingPage = "https://www.bing.com/translator";
    private const string BingTranslate = "https://www.bing.com/ttranslatev3";

    /// <summary>正文上限：超出部分不翻（详情正文本来也有上限，避免一次发几十 KB 上去）。</summary>
    private const int MaxCharacters = 6000;

    /// <summary>
    /// 单段长度。端点对一次请求的长度都有限制，超了会**只翻前面一部分**（实测用户遇到的就是这个：
    /// 长简介只翻了前半段，后半段还是英文）。所以统一切成 900 字以内的段落分别翻。
    /// </summary>
    private const int ChunkSize = 900;

    /// <summary>最多切几段：防止一篇超长自述打出十几个请求把额度烧光。</summary>
    private const int MaxChunks = 6;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TranslationCache _cache;
    private readonly Dictionary<string, string?> _memory = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;

    // 每个端点各自记冷却时间：实测 gtx 会持续 429，而 dict-chrome-ex / MyMemory / Bing 仍然可用。
    private DateTimeOffset _gtxCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _dictCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _myMemoryCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _bingCooldownUntil = DateTimeOffset.MinValue;

    // Bing 网页版翻译的临时令牌（约 10 分钟有效，需要 IG/IID + abuse-prevention key）
    private string? _bingIg;
    private string? _bingIid;
    private string? _bingKey;
    private string? _bingToken;
    private DateTimeOffset _bingTokenExpires = DateTimeOffset.MinValue;

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
        && DateTimeOffset.UtcNow < _bingCooldownUntil
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

        // 只有整段都翻成功才写缓存：半截译文一旦落盘，以后永远命中这半截
        if (result is not null && !result.EndsWith(PartialMarker, StringComparison.Ordinal))
        {
            _cache.Store(key, targetLanguage, normalized, result);
        }

        return result;
    }

    /// <summary>未翻完时追加的标记；带这个标记的结果不进缓存。</summary>
    private const string PartialMarker = "\u2026（后文未翻译）";

    /// <summary>
    /// 把正文切成小段，逐段走端点回退链，最后拼起来。
    ///
    /// 为什么要分段：端点对单次请求长度有限制，超长时不会报错，而是**只翻前面一部分**，
    /// 于是界面上出现"前半段中文、后半段英文"（用户实测）。
    /// </summary>
    private async Task<string?> RequestAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        var chunks = SplitForRequest(text, ChunkSize, MaxChunks);
        if (chunks.Count == 0) return null;

        var builder = new StringBuilder();
        var complete = true;

        for (var i = 0; i < chunks.Count; i++)
        {
            var part = await RequestChunkAsync(chunks[i], targetLanguage, cancellationToken).ConfigureAwait(true);
            if (part is null)
            {
                complete = false;
                break;
            }

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(part);
        }

        if (builder.Length == 0) return null;

        var translated = builder.ToString().Trim();
        return complete && translated.Length > 0 ? translated : translated + PartialMarker;
    }

    /// <summary>逐段走端点回退链（每个端点各自记冷却）。</summary>
    private async Task<string?> RequestChunkAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow >= _gtxCooldownUntil)
        {
            var viaGtx = await TryGtxAsync(text, targetLanguage, cancellationToken).ConfigureAwait(true);
            if (viaGtx is not null) return viaGtx;
        }

        if (DateTimeOffset.UtcNow >= _bingCooldownUntil)
        {
            var viaBing = await TryBingAsync(text, targetLanguage, cancellationToken).ConfigureAwait(true);
            if (viaBing is not null) return viaBing;
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

    /// <summary>
    /// Bing 网页版翻译：先抓 <c>/translator</c> 页面拿 IG / IID / abuse-prevention key 与 token，
    /// 再 POST 到 <c>ttranslatev3</c>。**不需要 API key**，token 约 10 分钟有效。
    /// 官方 Azure 翻译接口需要订阅密钥，这里用的是同一套翻译引擎的网页通道。
    /// </summary>
    private async Task<string?> TryBingAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        if (!await EnsureBingTokenAsync(cancellationToken).ConfigureAwait(true)) return null;

        try
        {
            var url = $"{BingTranslate}?isVertical=1&&IG={_bingIg}&IID={_bingIid}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Referer", BingPage);

            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fromLang"] = "auto-detect",
                ["to"] = BingLanguage(targetLanguage),
                ["text"] = text,
                ["token"] = _bingToken!,
                ["key"] = _bingKey!,
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(true);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.Forbidden)
            {
                _bingCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                _log?.Invoke($"翻译端点 bing 限流（{(int)response.StatusCode}）");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // token 过期时 Bing 常返回 400：清掉缓存，下次重新抓
                _bingTokenExpires = DateTimeOffset.MinValue;
                _log?.Invoke($"翻译端点 bing 返回 {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            if (!root[0].TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0)
            {
                return null;
            }

            var first = translations[0];
            return first.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? Normalize(textElement.GetString() ?? string.Empty)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"翻译端点 bing 失败：{ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>抓取/刷新 Bing 的临时令牌；失败返回 false。</summary>
    private async Task<bool> EnsureBingTokenAsync(CancellationToken cancellationToken)
    {
        if (_bingToken is not null && DateTimeOffset.UtcNow < _bingTokenExpires) return true;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BingPage);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return false;

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            var ig = System.Text.RegularExpressions.Regex.Match(html, "IG:\"([^\"]+)\"");
            var iid = System.Text.RegularExpressions.Regex.Match(html, "data-iid=\"([^\"]+)\"");
            var abuse = System.Text.RegularExpressions.Regex.Match(
                html, @"params_AbusePreventionHelper\s*=\s*\[(\d+),\s*""([^""]+)"",\s*(\d+)\]");

            if (!ig.Success || !iid.Success || !abuse.Success) return false;

            _bingIg = ig.Groups[1].Value;
            _bingIid = iid.Groups[1].Value;
            _bingKey = abuse.Groups[1].Value;
            _bingToken = abuse.Groups[2].Value;

            // abuse 数组第三项是有效毫秒数（通常 600000），留 60 秒余量
            if (long.TryParse(abuse.Groups[3].Value, out var lifetimeMs) && lifetimeMs > 60000)
            {
                _bingTokenExpires = DateTimeOffset.UtcNow.AddMilliseconds(lifetimeMs - 60000);
            }
            else
            {
                _bingTokenExpires = DateTimeOffset.UtcNow.AddMinutes(8);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"获取 bing 令牌失败：{ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>内部语言代码 → Bing 的写法（简体中文是 zh-Hans）。</summary>
    private static string BingLanguage(string language) => language.ToLowerInvariant() switch
    {
        "zh-cn" or "zh" => "zh-Hans",
        "zh-tw" => "zh-Hant",
        _ => language,
    };

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

    /// <summary>
    /// 把正文切成不超过 <paramref name="maxLength"/> 的片段（优先在句末/换行处断开）。
    /// 端点对长度敏感：超长请求会静默地只翻前半段。
    /// </summary>
    private static List<string> SplitForRequest(string text, int maxLength, int maxChunks)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            chunks.Add(current.ToString());
            current.Clear();
        }

        // 也按换行/制表符切：日文等没有空格的长正文才有多处断点
        foreach (var piece in text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length + piece.Length + 1 > maxLength && current.Length > 0)
            {
                Flush();
                if (chunks.Count >= maxChunks) return chunks;
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(piece);
        }

        Flush();
        return chunks;
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
