namespace NPMC.Launcher.Core.Mods;

/// <summary>Modrinth 上的一个版本文件。</summary>
public sealed record ModrinthFile(
    string FileName,
    string Url,
    string Sha1,
    long Size,
    bool Primary);

/// <summary>
/// 版本声明的依赖。
///
/// 三件事要分清：
///   - <c>required</c>：不装就跑不起来，必须解析（Fabric API 常见于此）
///   - <c>optional</c>：装了才有额外功能，**不能自动装**（用户没要求）
///   - <c>incompatible</c>：明确冲突
///
/// 依赖可能只给了项目（"要某个版本的这个项目"），也可能钉死了版本。
/// </summary>
public sealed record ModrinthDependency(
    string? VersionId,
    string? ProjectId,
    string DependencyType)
{
    public bool IsRequired =>
        string.Equals(DependencyType, "required", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Modrinth 上的一个项目。</summary>
public sealed record ModrinthProject(
    string Id,
    string Slug,
    string Title,
    string Description)
{
    /// <summary>界面上优先显示标题，标题为空时退回 slug。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Slug : Title;
}

/// <summary>搜索结果里的一条。</summary>
public sealed record ModrinthSearchHit(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string IconUrl,
    long Downloads,
    IReadOnlyList<string> Categories)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Slug : Title;
}

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
    /// <summary>该版本声明的依赖。用 init 而不是构造参数，避免破坏已有调用点。</summary>
    public IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = Array.Empty<ModrinthDependency>();
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
