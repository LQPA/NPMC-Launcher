namespace NPMC.Launcher.Core.Modpacks;

/// <summary>
/// 作者禁止第三方下载的整合包文件。
///
/// 拿不到下载地址，但 CurseForge 仍然提供文件名与 SHA1；启动器可以：
///   - 用 <see cref="PageUrl"/> 打开浏览器让用户手动下载；
///   - 监听下载目录，用 <see cref="Sha1"/> 校验后自动放进实例。
/// </summary>
public sealed record BlockedModFile(
    int ProjectId,
    int FileId,
    string ProjectName,
    string ProjectSlug,
    string FileName,
    string Sha1,
    string TargetFolder = "mods")
{
    /// <summary>CurseForge 的文件页（不是 CDN 直链，避免绕过作者的分发开关）。</summary>
    /// <summary>CurseForge 的栏目：模组 / 资源包 / 光影的网址前缀不同。</summary>
    private string Section => TargetFolder switch
    {
        "resourcepacks" => "texture-packs",
        "shaderpacks" => "shader-packs",
        _ => "mc-mods",
    };

    public string PageUrl =>
        $"https://www.curseforge.com/minecraft/{Section}/{ProjectSlug}/files/{FileId}";

    /// <summary>
    /// 项目主页。具体文件页可能 404（作者删了旧文件），但项目主页通常还在，
    /// 可以在 Files 里找最新兼容版本；所以手动补的时候优先打开这个。
    /// </summary>
    public string ProjectUrl =>
        string.IsNullOrWhiteSpace(ProjectSlug)
            ? ""
            : $"https://www.curseforge.com/minecraft/{Section}/{ProjectSlug}";

    public string RelativePath => $"{TargetFolder}/{FileName}";

    public string DisplayName => string.IsNullOrWhiteSpace(ProjectName) ? FileName : ProjectName;
}
