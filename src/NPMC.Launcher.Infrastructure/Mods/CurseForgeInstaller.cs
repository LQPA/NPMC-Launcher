using System.Globalization;
using NPMC.Launcher.Infrastructure.CurseForge;

namespace NPMC.Launcher.Infrastructure.Mods;

/// <summary>
/// 从 CurseForge 解析安装计划。
///
/// 与 Modrinth 的差异：
///   - 用数字 modId 而不是 slug；
///   - 文件列表通常不带下载地址，要单独调 /files/{id}/download-url 申请，
///     作者禁止第三方下载时拿不到 —— 那种文件无法自动安装，只能提示去网页；
///   - 依赖以 modId + relationType 给出（3 = 必需），只给了 modId，没钉版本。
/// </summary>
public sealed class CurseForgeInstaller
{
    private const int MaxNodes = 64;

    private readonly CurseForgeClient _client;

    public CurseForgeInstaller(CurseForgeClient client) => _client = client;

    /// <summary>未配置 API key 时返回 null（调用方据此给出可读提示）。</summary>
    public static CurseForgeInstaller? Create(HttpClient http)
    {
        if (!CurseForgeClient.TryResolveApiKey(out var apiKey, out _) || apiKey is null) return null;
        return new CurseForgeInstaller(new CurseForgeClient(http, apiKey));
    }

    public async Task<InstallPlan> ResolveAsync(
        int modId,
        string gameVersion,
        IReadOnlyList<string> loaders,
        bool includeDependencies = true,
        string rootTitle = "",
        CancellationToken cancellationToken = default)
    {
        var targets = new List<InstallTarget>();
        var visited = new HashSet<int>();

        var queue = new Queue<(int ModId, bool IsDependency)>();
        queue.Enqueue((modId, false));

        while (queue.Count > 0 && targets.Count < MaxNodes)
        {
            var (currentModId, isDependency) = queue.Dequeue();
            if (!visited.Add(currentModId)) continue;

            IReadOnlyList<CurseForgeFile> files;
            try
            {
                files = await _client
                    .GetFilesAsync(currentModId, gameVersion, loaders, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var newest = files.FirstOrDefault();
            if (newest is null) continue;

            var url = newest.DownloadUrl
                      ?? await _client.TryGetDownloadUrlAsync(newest.ModId, newest.FileId, cancellationToken)
                          .ConfigureAwait(false);

            targets.Add(new InstallTarget(
                newest.ModId.ToString(CultureInfo.InvariantCulture),
                isDependency ? "" : rootTitle,
                string.IsNullOrWhiteSpace(newest.DisplayName) ? newest.FileName : newest.DisplayName,
                newest.FileName,
                url ?? "",
                newest.Sha1,
                isDependency));

            if (!includeDependencies) continue;

            foreach (var dependency in newest.Dependencies.Where(dependency => dependency.IsRequired))
                queue.Enqueue((dependency.ModId, true));
        }

        return new InstallPlan(targets);
    }
}
