using System.IO.Compression;
using NPMC.Launcher.Core.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class ModpackArchiveFormatTests
{
    [Fact]
    public void Mrpack_is_detected_by_its_index()
    {
        var path = CreateZip("mrpack", ("modrinth.index.json", "{}"));

        Assert.Equal(ModpackArchiveFormat.Modrinth, ModpackArchiveFormatDetector.Detect(path));
    }

    [Fact]
    public void CurseForge_zip_is_detected_by_manifest()
    {
        var path = CreateZip("cf", ("manifest.json", "{}"), ("overrides/config/a.txt", "x"));

        Assert.Equal(ModpackArchiveFormat.CurseForge, ModpackArchiveFormatDetector.Detect(path));
    }

    [Fact]
    public void Unknown_when_neither_manifest_exists()
    {
        var path = CreateZip("plain", ("readme.txt", "hi"));

        Assert.Equal(ModpackArchiveFormat.Unknown, ModpackArchiveFormatDetector.Detect(path));
    }

    [Fact]
    public void Unknown_for_missing_or_non_zip_files()
    {
        var missing = Path.Combine(Path.GetTempPath(), "npmc-missing-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.Equal(ModpackArchiveFormat.Unknown, ModpackArchiveFormatDetector.Detect(missing));

        var text = Path.Combine(Path.GetTempPath(), "npmc-text-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(text, "not a zip");

        try
        {
            Assert.Equal(ModpackArchiveFormat.Unknown, ModpackArchiveFormatDetector.Detect(text));
        }
        finally
        {
            try { File.Delete(text); } catch { }
        }
    }

    [Fact]
    public void Modrinth_index_wins_when_both_exist()
    {
        var path = CreateZip("both", ("modrinth.index.json", "{}"), ("manifest.json", "{}"));

        Assert.Equal(ModpackArchiveFormat.Modrinth, ModpackArchiveFormatDetector.Detect(path));
    }

    private static string CreateZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"npmc-{name}-{Guid.NewGuid():N}.zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }
}
