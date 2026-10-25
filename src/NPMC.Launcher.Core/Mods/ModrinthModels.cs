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
}

/// <summary>本地一个模组文件，以及它在 Modrinth 上对应的版本（查不到则为 null）。</summary>
public sealed record LocalModInfo(
    string Path,
    string FileName,
    string Sha1,
    ModrinthVersion? MatchedVersion);

/// <summary>一条可用的升级建议。</summary>
public sealed record ModUpdate(
    string LocalPath,
    string FileName,
    ModrinthVersion Current,
    ModrinthVersion Latest)
{
    public string ProjectId => Current.ProjectId;

    public string Description =>
        $"{FileName}: {Current.VersionNumber} → {Latest.VersionNumber}";
}
