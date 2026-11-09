using System.IO.Compression;

namespace NPMC.Launcher.Core.Modpacks;

/// <summary>本地整合包文件的格式。</summary>
public enum ModpackArchiveFormat
{
    Unknown = 0,

    /// <summary>Modrinth 的 .mrpack：有 modrinth.index.json。</summary>
    Modrinth = 1,

    /// <summary>CurseForge 的导出包（.zip）：有 manifest.json，没有 modrinth.index.json。</summary>
    CurseForge = 2,

    /// <summary>
    /// MultiMC / Prism / HMCL 的实例导出包：<c>mmc-pack.json</c> + <c>instance.cfg</c>，
    /// 文件全部打包在 <c>.minecraft/</c>（Prism 也有用 <c>minecraft/</c> 的）里，安装不需要联网下载。
    /// </summary>
    MultiMc = 3,
}

/// <summary>
/// 按压缩包内的清单文件识别本地整合包格式。
/// 三种格式的安装入口不同：
///   - mrpack：清单里直接有下载地址；
///   - CF：只有 (projectID, fileID)，要查 API；
///   - MultiMC 系列：文件就在包里，解压即可。
/// </summary>
public static class ModpackArchiveFormatDetector
{
    public const string ModrinthIndexEntry = "modrinth.index.json";
    public const string CurseForgeManifestEntry = "manifest.json";
    public const string MultiMcPackEntry = "mmc-pack.json";
    public const string MultiMcInstanceEntry = "instance.cfg";

    /// <summary>识别失败（文件不存在 / 不是 zip / 都不匹配）返回 Unknown。</summary>
    public static ModpackArchiveFormat Detect(string archivePath)
    {
        try
        {
            if (!File.Exists(archivePath)) return ModpackArchiveFormat.Unknown;

            using var archive = ZipFile.OpenRead(archivePath);

            if (archive.GetEntry(ModrinthIndexEntry) is not null)
                return ModpackArchiveFormat.Modrinth;

            if (archive.GetEntry(CurseForgeManifestEntry) is not null)
                return ModpackArchiveFormat.CurseForge;

            // HMCL 用 MultiMC 格式导出的包两个文件都有；有些工具只写其中一个
            if (archive.GetEntry(MultiMcPackEntry) is not null
                || archive.GetEntry(MultiMcInstanceEntry) is not null)
                return ModpackArchiveFormat.MultiMc;

            return ModpackArchiveFormat.Unknown;
        }
        catch
        {
            return ModpackArchiveFormat.Unknown;
        }
    }
}
