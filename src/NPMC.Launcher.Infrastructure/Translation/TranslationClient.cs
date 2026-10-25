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
/// 缓存按 (目标语言 + 原文) 的 SHA1 落盘：同一段简介翻一次就够，
/// 而且不同项目常有相同的模板化描述，去重能省不少请求。
/// </summary>
public sealed class TranslationClient
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    /// <summary>单次翻译的字符上限。超长正文会先截断 —— 详情摘要本来就只显示前几百字。</summary>
    private const int MaxCharacters = 1800;

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly Dictionary<string, string?> _memory = new(StringComparer.Ordinal);

    public TranslationClient(HttpClient http, string cacheDirectory)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
    }

    public async Task<string?> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (string.IsNullOrWhiteSpace(targetLanguage)) return null;

        var normalized = text.Length > MaxCharacters ? text[..MaxCharacters] : text;
        var key = $"{targetLanguage}|{normalized}";

        if (_memory.TryGetValue(key, out var cached)) return cached;

        var file = Path.Combine(_cacheDirectory, Hash(key) + ".txt");

        try
        {
            if (File.Exists(file))
            {
                var fromDisk = (await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(true)).Trim();
                if (fromDisk.Length > 0)
                {
                    _memory[key] = fromDisk;
                    return fromDisk;
                }
            }
        }
        catch
        {
            // 缓存读不了就当没有
        }

        var result = await RequestAsync(normalized, targetLanguage, cancellationToken).ConfigureAwait(true);
        _memory[key] = result;

        if (result is not null)
        {
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllTextAsync(file, result, cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                // 落盘失败不影响显示
            }
        }

        return result;
    }

    private async Task<string?> RequestAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{Endpoint}?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}"
                      + $"&dt=t&q={Uri.EscapeDataString(text)}";

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(true);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 响应形如 [[["译文","原文",...], ...], null, "en", ...]
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

            var translated = builder.ToString().Trim();
            return translated.Length == 0 ? null : translated;
        }
        catch
        {
            // 端点不可用 / 限流 / 网络问题 —— 一律当作"没有翻译"，不冒泡
            return null;
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
