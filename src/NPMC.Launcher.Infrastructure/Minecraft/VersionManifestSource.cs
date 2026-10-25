using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Models;

namespace NPMC.Launcher.Infrastructure.Minecraft;

public sealed record VersionManifestEntry(
    string Id,
    string Type,
    string Url,
    string Sha1,
    DateTimeOffset ReleaseTime);

public sealed record VersionManifest(
    IReadOnlyList<VersionManifestEntry> Versions,
    string LatestRelease,
    string LatestSnapshot)
{
    public VersionManifestEntry? Find(string id) =>
        Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 拉取 Mojang 官方的版本清单与单版本元数据。
/// 清单 URL 可被镜像替换（BMCLAPI 等），但解析逻辑完全一致。
/// </summary>
public sealed class VersionManifestSource
{
    public const string OfficialManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string, ResourceKind, CancellationToken, Task<string>> _fetch;

    /// <summary>单一地址的构造：用于镜像测试自身的参照，避免参照值也被镜像影响。</summary>
    public VersionManifestSource(Func<string, CancellationToken, Task<string>> fetch)
        : this((url, _, ct) => fetch(url, ct))
    {
    }

    /// <summary>按资源类别选择下载源（清单 / 版本 JSON 各对应一条镜像规则）。</summary>
    public VersionManifestSource(Func<string, ResourceKind, CancellationToken, Task<string>> fetch)
        => _fetch = fetch;

    /// <summary>
    /// 按镜像路由构建：逐个候选地址尝试，全部失败才抛出。
    ///
    /// 命令行与界面都要这样构造，所以逻辑放在这里而不是各自的调用点 ——
    /// 否则"镜像失败要回退官方"这件事会出现两份实现，其中一份迟早忘记更新。
    /// </summary>
    public static VersionManifestSource Create(MirrorRouter router, Http.HttpFileDownloader downloader)
    {
        return new VersionManifestSource(async (url, kind, cancellationToken) =>
        {
            Exception? last = null;

            foreach (var target in router.Resolve(url, kind))
            {
                try
                {
                    return await downloader.GetStringAsync(target.Url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                }
            }

            throw last ?? new InvalidOperationException($"无法获取 {url}");
        });
    }

    public async Task<VersionManifest> GetManifestAsync(string? url = null, CancellationToken ct = default)
    {
        var json = await _fetch(url ?? OfficialManifestUrl, ResourceKind.VersionManifest, ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions)
                  ?? throw new InvalidDataException("版本清单解析失败。");

        var versions = dto.Versions
            .Select(v => new VersionManifestEntry(
                v.Id, v.Type, v.Url, v.Sha1,
                DateTimeOffset.TryParse(v.ReleaseTime, out var t) ? t : default))
            .ToList();

        return new VersionManifest(versions, dto.Latest.Release, dto.Latest.Snapshot);
    }

    public async Task<VersionJson> GetVersionJsonAsync(VersionManifestEntry entry, CancellationToken ct = default)
    {
        var json = await _fetch(entry.Url, ResourceKind.VersionMetadata, ct).ConfigureAwait(false);
        return VersionJsonSerializer.Parse(json);
    }

    // ---- 清单 DTO ----
    private sealed class ManifestDto
    {
        [JsonPropertyName("latest")] public LatestDto Latest { get; set; } = new();
        [JsonPropertyName("versions")] public List<VersionDto> Versions { get; set; } = new();
    }

    private sealed class LatestDto
    {
        [JsonPropertyName("release")] public string Release { get; set; } = "";
        [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
    }

    private sealed class VersionDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
        [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }
    }
}
