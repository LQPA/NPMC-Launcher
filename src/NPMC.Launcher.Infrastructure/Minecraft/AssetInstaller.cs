using System.Collections.Concurrent;
using System.Text.Json;
using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Http;

namespace NPMC.Launcher.Infrastructure.Minecraft;

public sealed record AssetProgress(int Completed, int Total, long BytesDownloaded);

public sealed record AssetInstallResult(
    int Total,
    int Downloaded,
    int Skipped,
    long BytesDownloaded,
    IReadOnlyList<string> Failures);

/// <summary>
/// 按 assetIndex 下载游戏资源对象到 &lt;root&gt;\assets\objects\&lt;前2位&gt;\&lt;hash&gt;。
///
/// 实测基准（1.20.1 / assetIndex "5"）：3598 个对象、621.5 MB。
///
/// 两个实现要点：
///   1. **资源对象的 hash 本身就是 SHA1**，所以 URL 里的 hash 直接可以当校验值用，
///      不需要额外的校验字段——这让"已存在即跳过"的判断非常廉价。
///   2. 3598 个文件必须并发。串行时大量时间耗在 HTTP 往返上，
///      而且按文件数显示进度会假性卡死（大文件集中在少数几个），所以进度按字节优先。
/// </summary>
public sealed class AssetInstaller
{
    public const string OfficialObjectBase = "https://resources.download.minecraft.net";

    private readonly HttpFileDownloader _downloader;
    private readonly MirrorRouter? _router;

    /// <param name="router">为 null 时只走官方资源源；传入时按 AssetObject 规则生成候选地址。</param>
    public AssetInstaller(HttpFileDownloader downloader, MirrorRouter? router = null)
    {
        _downloader = downloader;
        _router = router;
    }

    public async Task<AssetInstallResult> InstallAsync(
        LauncherPaths paths,
        string assetIndexId,
        int concurrency = 16,
        string? objectBaseUrl = null,
        IProgress<AssetProgress>? progress = null,
        CancellationToken ct = default)
    {
        var indexPath = paths.AssetIndexFile(assetIndexId);
        if (!File.Exists(indexPath))
            throw new FileNotFoundException($"缺少 assetIndex 文件，请先执行 install：{indexPath}", indexPath);

        var json = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
        var objects = ReadObjects(json);

        var baseUrl = (objectBaseUrl ?? OfficialObjectBase).TrimEnd('/');
        var total = objects.Count;

        var downloaded = new int[1];
        var skipped = new int[1];
        var completed = new int[1];
        var bytes = new long[1];
        var failures = new ConcurrentBag<string>();

        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var tasks = objects.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var url = $"{baseUrl}/{item.Hash[..2]}/{item.Hash}";
                var destination = paths.AssetObjectFile(item.Hash);

                var candidates = _router is null
                    ? new[] { url }
                    : _router.Resolve(url, ResourceKind.AssetObject).Select(target => target.Url).ToArray();

                var outcome = await _downloader
                    .EnsureFromAsync(candidates, destination, item.Hash, ct)
                    .ConfigureAwait(false);
                var wrote = outcome.Downloaded;

                if (wrote)
                {
                    Interlocked.Increment(ref downloaded[0]);
                    Interlocked.Add(ref bytes[0], item.Size);
                }
                else
                {
                    Interlocked.Increment(ref skipped[0]);
                }

                Interlocked.Increment(ref completed[0]);
                progress?.Report(new AssetProgress(
                    Volatile.Read(ref completed[0]), total, Interlocked.Read(ref bytes[0])));
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Hash}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new AssetInstallResult(
            total,
            downloaded[0],   // int 读取本身是原子的，Interlocked.Read 只支持 long
            skipped[0],
            Interlocked.Read(ref bytes[0]),
            failures.ToList());
    }

    private sealed record AssetObject(string Hash, long Size);

    private static List<AssetObject> ReadObjects(string indexJson)
    {
        var list = new List<AssetObject>();
        using var document = JsonDocument.Parse(indexJson);

        if (!document.RootElement.TryGetProperty("objects", out var objects)) return list;

        foreach (var entry in objects.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("hash", out var hashElement)) continue;
            var hash = hashElement.GetString();
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

            long size = 0;
            if (entry.Value.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s))
                size = s;

            list.Add(new AssetObject(hash, size));
        }

        return list;
    }
}
