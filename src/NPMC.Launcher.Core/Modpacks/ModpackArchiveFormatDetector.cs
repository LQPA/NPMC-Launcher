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
}

/// <summary>
/// 按压缩包内的清单文件识别本地整合包格式。
/// 两种格式的安装入口不同：mrpack 直接有下载地址，CF 只有 (projectID, fileID) 要查 API。
/// </summary>
public static class ModpackArchiveFormatDetector
{
    public const string ModrinthIndexEntry = "modrinth.index.json";
    public const string CurseForgeManifestEntry = "manifest.json";

    /// <summary>识别失败（文件不存在 / 不是 zip / 两者都没有）返回 Unknown。</summary>
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

            return ModpackArchiveFormat.Unknown;
        }
        catch
        {
            return ModpackArchiveFormat.Unknown;
        }
    }
}
