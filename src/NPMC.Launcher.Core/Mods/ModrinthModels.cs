namespace NPMC.Launcher.Core.Mods;

/// <summary>Modrinth 上的一个版本文件。</summary>
public sealed record ModrinthFile(
    string FileName,
    string Url,
    string Sha1,
    long Size,
    bool Primary);

/// <summary>Modrinth 上的一个版本。</summary>
public sealed record ModrinthVersion(
    string Id,
    string ProjectId,
    string VersionNumber,
    string Name,
    string VersionType,
    DateTimeOffset PublishedAt,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModrinthFile> Files)
{
    /// <summary>主文件（Modrinth 通常标一个 primary；没有标注时取第一个）。</summary>
    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(file => file.Primary) ?? Files.FirstOrDefault();

    /// <summary>是否为预发布版本（beta / alpha）。</summary>
    public bool IsPrerelease =>
        !string.Equals(VersionType, "release", StringComparison.OrdinalIgnoreCase);

    /// <summary>转换成与平台无关的候选版本；没有可下载文件时返回 null。</summary>
    public UpdateCandidate? ToCandidate()
    {
        if (PrimaryFile is not { } file) return null;
        if (string.IsNullOrWhiteSpace(file.Url)) return null;

        return new UpdateCandidate
        {
            Source = UpdateSource.Modrinth,
            ProjectId = ProjectId,
            VersionLabel = VersionNumber,
            PublishedAt = PublishedAt,
            FileName = file.FileName,
            DownloadUrl = file.Url,
            Sha1 = string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1,
            IsPrerelease = IsPrerelease,
            GameVersions = GameVersions,
            Loaders = Loaders,
        };
    }
}
