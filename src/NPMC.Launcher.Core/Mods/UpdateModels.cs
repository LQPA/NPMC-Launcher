namespace NPMC.Launcher.Core.Mods;

/// <summary>模组内容来自哪个平台。</summary>
public enum UpdateSource
{
    Modrinth = 0,
    CurseForge = 1,
}

/// <summary>
/// 项目的跨平台标识。两个平台的 id 形态不同（Modrinth 是 base62 短串，CurseForge 是整数），
/// 所以做字典键时必须带上来源，否则不同平台的项目理论上会撞号。
/// </summary>
public sealed record UpdateProjectKey(UpdateSource Source, string ProjectId)
{
    public override string ToString() => $"{Source}:{ProjectId}";
}

/// <summary>
/// 与平台无关的版本信息。
///
/// 为什么需要它：Modrinth 用 SHA1 识别文件、返回结构化版本号；
/// CurseForge 用 MurmurHash2 指纹识别、版本信息挂在文件上，且下载地址要另行申请。
/// 升级判定只关心"发布时间、版本标签、能不能下载"，所以先把这些抽出来，
/// 判定逻辑就不必知道数据来自哪个平台。
/// </summary>
public sealed record UpdateCandidate
{
    public required UpdateSource Source { get; init; }

    /// <summary>平台内的项目 id（Modrinth projectId / CurseForge modId）。</summary>
    public required string ProjectId { get; init; }

    /// <summary>显示用的版本标签：Modrinth 的 version_number，或 CurseForge 的文件名。</summary>
    public required string VersionLabel { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public required string FileName { get; init; }

    /// <summary>下载地址。CurseForge 在作者禁止第三方下载时可能拿不到，此时为空。</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>已知的 SHA1（CurseForge 通常没有）。用于下载后校验。</summary>
    public string? Sha1 { get; init; }

    public bool IsPrerelease { get; init; }

    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();

    /// <summary>是否与另一个候选"是同一个项目的同一个版本"。</summary>
    public bool IsSameVersion(UpdateCandidate other) =>
        Source == other.Source
        && string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal)
        && string.Equals(VersionLabel, other.VersionLabel, StringComparison.OrdinalIgnoreCase);

    public UpdateProjectKey Key => new(Source, ProjectId);
}

/// <summary>本地一个模组文件，以及它在某个平台上对应的版本（认领不到则为 null）。</summary>
public sealed record LocalModInfo
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required string Sha1 { get; init; }

    /// <summary>平台认领到的"当前版本"。为 null 表示这个文件在两个平台都查不到。</summary>
    public UpdateCandidate? Matched { get; init; }
}

/// <summary>一条升级建议。</summary>
public sealed record ModUpdate(
    string LocalPath,
    string FileName,
    UpdateCandidate Current,
    UpdateCandidate Latest)
{
    public UpdateSource Source => Latest.Source;

    public string Description => $"{FileName}: {Current.VersionLabel} → {Latest.VersionLabel}";
}
