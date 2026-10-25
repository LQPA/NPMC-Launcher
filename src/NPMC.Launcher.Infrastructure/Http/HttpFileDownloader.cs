using System.Net;
using System.Security.Cryptography;

namespace NPMC.Launcher.Infrastructure.Http;

/// <summary>一次失败的下载尝试。</summary>
public sealed record DownloadAttempt(string Url, string Error);

/// <summary>下载结果。Url 是实际成功的地址；跳过已存在时为空字符串。</summary>
public sealed record DownloadOutcome(bool Downloaded, string Url, IReadOnlyList<DownloadAttempt> Attempts);

/// <summary>
/// 带 SHA1 校验、原子落盘、候选地址回退的下载器。
///
/// 设计要点（都是实测踩出来的）：
///   - 原子写入：先写 .tmp 再 File.Move(overwrite)，避免半截文件被当成"已安装"
///   - 已存在且 SHA1 匹配直接跳过，因此断点续传天然成立
///   - **候选地址按顺序尝试**：镜像入口会 302 到带过期签名的节点，任何一跳失败都要能退回下一个候选
///   - 显式 User-Agent：Modrinth 等 API 依赖它做限流归属
/// </summary>
public sealed class HttpFileDownloader
{
    private readonly HttpClient _http;

    public HttpFileDownloader(HttpClient http) => _http = http;

    public static HttpClient CreateClient(string userAgent = "NPMC-Launcher/0.1 (github.com/LQPA/NPMC-Launcher)")
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 16,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>单地址便捷重载。</summary>
    public async Task<bool> EnsureAsync(string url, string destPath, string? sha1, CancellationToken ct = default)
        => (await EnsureFromAsync(new[] { url }, destPath, sha1, ct).ConfigureAwait(false)).Downloaded;

    /// <summary>
    /// 依次尝试候选地址，第一个成功的即落盘。
    /// 返回 Downloaded=false 表示本地文件已存在且校验通过、本次没有下载。
    /// 全部失败时抛 <see cref="InvalidOperationException"/>，消息里带每个候选的失败原因。
    /// </summary>
    public async Task<DownloadOutcome> EnsureFromAsync(
        IReadOnlyList<string> candidateUrls,
        string destPath,
        string? sha1,
        CancellationToken ct = default)
    {
        if (candidateUrls.Count == 0)
            throw new ArgumentException("至少需要一个候选地址。", nameof(candidateUrls));

        var directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(destPath) &&
            (string.IsNullOrEmpty(sha1) || await MatchesSha1Async(destPath, sha1, ct).ConfigureAwait(false)))
        {
            return new DownloadOutcome(false, string.Empty, Array.Empty<DownloadAttempt>());
        }

        var attempts = new List<DownloadAttempt>(candidateUrls.Count);
        var tempPath = destPath + ".tmp";

        foreach (var url in candidateUrls)
        {
            try
            {
                await using (var source = await _http.GetStreamAsync(url, ct).ConfigureAwait(false))
                await using (var target = File.Create(tempPath))
                {
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(sha1) && !await MatchesSha1Async(tempPath, sha1, ct).ConfigureAwait(false))
                    throw new InvalidDataException("SHA1 校验失败");

                File.Move(tempPath, destPath, overwrite: true);
                return new DownloadOutcome(true, url, attempts);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                attempts.Add(new DownloadAttempt(url, ex.Message));
                TryDelete(tempPath);
            }
        }

        var detail = string.Join(" | ", attempts.Select(attempt => $"{attempt.Url} → {attempt.Error}"));
        throw new InvalidOperationException($"全部 {candidateUrls.Count} 个候选地址都失败：{detail}");
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
        await _http.GetStringAsync(url, ct).ConfigureAwait(false);

    private static async Task<bool> MatchesSha1Async(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }
}
