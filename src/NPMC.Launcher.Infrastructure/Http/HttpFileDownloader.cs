using System.Net;
using System.Security.Cryptography;

namespace NPMC.Launcher.Infrastructure.Http;

/// <summary>
/// 带 SHA1 校验与原子落盘的下载器。
///
/// 设计要点（都是实测踩出来的）：
///   - 原子写入：先写 .tmp 再 File.Move(overwrite)，避免半截文件被当成"已安装"
///   - 已存在且 SHA1 匹配直接跳过，因此断点续传天然成立
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

    /// <summary>
    /// 确保目标文件存在且校验通过。
    /// 返回 true 表示本次真的下载了，false 表示已存在且校验通过而跳过。
    /// </summary>
    public async Task<bool> EnsureAsync(string url, string destPath, string? sha1, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(destPath) && (string.IsNullOrEmpty(sha1) || await MatchesSha1Async(destPath, sha1, ct)))
            return false;

        var tempPath = destPath + ".tmp";

        await using (var source = await _http.GetStreamAsync(url, ct).ConfigureAwait(false))
        await using (var target = File.Create(tempPath))
        {
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(sha1) && !await MatchesSha1Async(tempPath, sha1, ct))
        {
            File.Delete(tempPath);
            throw new InvalidDataException($"SHA1 校验失败: {destPath}");
        }

        File.Move(tempPath, destPath, overwrite: true);
        return true;
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
        await _http.GetStringAsync(url, ct).ConfigureAwait(false);

    private static async Task<bool> MatchesSha1Async(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
