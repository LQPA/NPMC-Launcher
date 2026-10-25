using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace NPMC.Launcher.Infrastructure.Mirrors;

public sealed record ProbeHop(int StatusCode, string Url, string? Location);

/// <summary>一次探测的完整结果，包含跳转链。</summary>
public sealed record ProbeResult(
    string RequestedUrl,
    IReadOnlyList<ProbeHop> Hops,
    bool Success,
    int FinalStatus,
    string? ContentType,
    long BytesRead,
    TimeSpan Elapsed,
    string? Error)
{
    public string FinalUrl => Hops.Count > 0 ? Hops[^1].Url : RequestedUrl;

    public int RedirectCount => Math.Max(0, Hops.Count - 1);
}

/// <summary>
/// 探测下载地址：手动跟随重定向并记录每一跳。
///
/// 关键：**必须关掉 HttpClient 的自动重定向**（AllowAutoRedirect = false）。
/// 默认开启时 HttpClient 会自己把 302 跟完，调用方只看到最后一步，
/// 那样"跳转链"这个最需要观察的信息就丢了。
///
/// 镜像的实际拓扑是"入口 → 302 → CERNET / 签名节点 → 文件"，
/// 只有把每一跳打出来，才能判断它到底把请求转到了哪里
/// （例如 client.jar 最终可能被转回 piston-data.mojang.com）。
/// </summary>
public sealed class MirrorProbe
{
    private const int MaxRedirects = 6;

    private readonly HttpClient _http;

    /// <summary>默认构造：自带一个关闭自动重定向的客户端。</summary>
    public MirrorProbe()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NPMC-Launcher/0.1 (mirror-probe)");
    }

    /// <summary>注入构造：仅用于测试，调用方需自行保证 AllowAutoRedirect = false。</summary>
    public MirrorProbe(HttpClient http) => _http = http;

    public async Task<ProbeResult> ProbeAsync(string url, int sampleBytes = 1024, CancellationToken cancellationToken = default)
    {
        var hops = new List<ProbeHop>();
        var current = url;
        var stopwatch = Stopwatch.StartNew();

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            // 只取前若干字节，避免真的把 22MB 的 client.jar 拉下来
            request.Headers.Range = new RangeHeaderValue(0, Math.Max(0, sampleBytes - 1));

            HttpResponseMessage response;
            try
            {
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ProbeResult(url, hops, false, 0, null, 0, stopwatch.Elapsed, Describe(ex));
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var location = response.Headers.Location?.ToString();
                hops.Add(new ProbeHop(status, current, location));

                if (status is >= 300 and < 400 && location is not null)
                {
                    current = new Uri(new Uri(current), location).ToString();
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                long bytesRead = 0;

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        var buffer = new byte[sampleBytes];
                        bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        return new ProbeResult(url, hops, false, status, contentType, 0, stopwatch.Elapsed, Describe(ex));
                    }
                }

                stopwatch.Stop();
                return new ProbeResult(
                    url, hops, response.IsSuccessStatusCode, status, contentType,
                    bytesRead, stopwatch.Elapsed, null);
            }
        }

        stopwatch.Stop();
        return new ProbeResult(url, hops, false, 0, null, 0, stopwatch.Elapsed,
            $"重定向超过 {MaxRedirects} 次");
    }

    /// <summary>把内层异常一并带上——SSL 失败的真实原因往往在 InnerException 里。</summary>
    private static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            parts.Add(current.Message);

        return string.Join(" ← ", parts);
    }
}
