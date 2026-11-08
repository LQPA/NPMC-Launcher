using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.App.Diagnostics;

/// <summary>
/// 一次游戏启动的日志会话。启动时建目录，退出后把本次相关文件复制进来，
/// 避免 latest.log / crash-report 混到上一次启动。
/// </summary>
public sealed class LaunchLogSession
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private readonly string _launcherRoot;
    private readonly DateTimeOffset _startedAt;
    private readonly bool _detailedDiagnostics;

    private LaunchLogSession(
        string launchDirectory,
        string launcherRoot,
        string instanceName,
        string versionId,
        DateTimeOffset startedAt,
        bool detailedDiagnostics)
    {
        DirectoryPath = launchDirectory;
        _launcherRoot = launcherRoot;
        _startedAt = startedAt;
        _detailedDiagnostics = detailedDiagnostics;
        InstanceName = instanceName;
        VersionId = versionId;
    }

    public string DirectoryPath { get; }

    public string InstanceName { get; }

    public string VersionId { get; }

    public string LatestLogPath => Path.Combine(DirectoryPath, "latest.log");

    public string StderrLogPath => Path.Combine(DirectoryPath, "nmpc-stderr.log");

    public string CrashReportDirectory => Path.Combine(DirectoryPath, "crash-reports");

    public string CrashLogPath => Path.Combine(DirectoryPath, "crash.log");

    public string DebugLogPath => Path.Combine(DirectoryPath, "debug.log");

    public static LaunchLogSession Create(
        string launcherRoot,
        string instanceName,
        string versionId,
        string gameDirectory,
        string javaPath,
        DateTimeOffset startedAt,
        bool detailedDiagnostics = false)
    {
        var safeName = Sanitize(instanceName);
        var folder = $"{startedAt.ToLocalTime():yyyyMMdd-HHmmss}-{safeName}";
        var directory = Path.Combine(launcherRoot, "logs", "launches", folder);
        Directory.CreateDirectory(directory);

        var session = new LaunchLogSession(directory, launcherRoot, instanceName, versionId, startedAt, detailedDiagnostics);
        session.WriteJson("launch.json", new
        {
            instance = instanceName,
            version = versionId,
            game_directory = gameDirectory,
            java = javaPath,
            started_at = startedAt,
            detailed_diagnostics = detailedDiagnostics,
        });
        File.WriteAllText(
            Path.Combine(directory, "README.txt"),
            "本次启动的归档目录。\r\n\r\n" +
            "- crash.log：崩溃时生成的完整可读报告（诊断 + crash-report + latest.log + stderr）。\r\n" +
            "- crash-reports：仅放 Minecraft 原生生成的 crash-report；没有文件表示本次没有生成原生报告。\r\n" +
            "- latest.log / nmpc-stderr.log：本次游戏日志和启动器 stderr。\r\n" +
            "- 导出 ZIP 后直接查看 crash.log 即可。\r\n",
            Utf8Bom);
        return session;
    }

    public void Capture(
        string gameDirectory,
        int exitCode,
        string? diagnosis,
        IReadOnlyCollection<string>? enabledMods = null)
    {
        try
        {
            CopyIfExists(Path.Combine(gameDirectory, "logs", "latest.log"), "latest.log");
            CopyIfExists(Path.Combine(gameDirectory, "logs", "nmpc-stderr.log"), "nmpc-stderr.log");
            CopyIfExists(Path.Combine(gameDirectory, "logs", "nmpc-stdout.log"), "nmpc-stdout.log");
            CopyIfExists(Path.Combine(gameDirectory, "logs", "nmpc-command.txt"), "nmpc-command.txt");

            if (_detailedDiagnostics)
            {
                CopyIfExists(Path.Combine(gameDirectory, "logs", "debug.log"), "debug.log");
                CopyDirectoryIfExists(
                    Path.Combine(gameDirectory, ".mixin.out"),
                    Path.Combine(DirectoryPath, ".mixin.out"));

                foreach (var hsErr in Directory.EnumerateFiles(gameDirectory, "hs_err_pid*.log"))
                {
                    if (File.GetLastWriteTimeUtc(hsErr) >= _startedAt.UtcDateTime.AddSeconds(-2))
                    {
                        File.Copy(hsErr, Path.Combine(DirectoryPath, Path.GetFileName(hsErr)), overwrite: true);
                    }
                }
            }

            var crashSource = Path.Combine(gameDirectory, "crash-reports");
            var crashTarget = Path.Combine(DirectoryPath, "crash-reports");
            if (Directory.Exists(crashSource))
            {
                foreach (var file in Directory.EnumerateFiles(crashSource, "crash-*.txt"))
                {
                    // 只归档本次启动后写出的崩溃报告。
                    if (File.GetLastWriteTimeUtc(file) < _startedAt.UtcDateTime.AddSeconds(-2))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(crashTarget);
                    File.Copy(file, Path.Combine(crashTarget, Path.GetFileName(file)), overwrite: true);
                }
            }

            WriteJson("result.json", new
            {
                outcome = exitCode == 0 ? "normal" : "crash",
                exit_code = exitCode,
                ended_at = DateTimeOffset.UtcNow,
                diagnosis,
                enabled_mods = enabledMods,
            });
            File.WriteAllText(
                Path.Combine(DirectoryPath, "diagnosis.txt"),
                diagnosis ?? string.Empty,
                Utf8Bom);

            PruneOldSessions();
        }
        catch
        {
            // 归档失败不能影响退出流程
        }
    }

    public void SetDiagnosis(string diagnosis)
    {
        try
        {
            File.WriteAllText(Path.Combine(DirectoryPath, "diagnosis.txt"), diagnosis, Utf8Bom);
        }
        catch
        {
        }
    }

    /// <summary>为本次异常退出生成一个可直接阅读/发送的 crash.log。</summary>
    public string WriteCrashLog(LaunchDiagnosisReport report)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("NPMC Launcher 崩溃日志");
        builder.AppendLine($"实例：{InstanceName}");
        builder.AppendLine($"版本：{VersionId}");
        builder.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine("== 诊断 ==");
        builder.AppendLine(report.Title);
        builder.AppendLine(report.Summary);
        if (!string.IsNullOrWhiteSpace(report.Evidence))
        {
            builder.AppendLine();
            builder.AppendLine("证据：");
            builder.AppendLine(report.Evidence);
        }

        builder.AppendLine();
        builder.AppendLine("建议：");
        builder.AppendLine(report.Suggestion);

        var crashReport = FindNewestCrashReport();
        if (crashReport is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"== crash-report：{Path.GetFileName(crashReport)} ==");
            builder.AppendLine(ReadTailText(crashReport, 240));
        }

        if (File.Exists(LatestLogPath))
        {
            builder.AppendLine();
            builder.AppendLine("== latest.log（尾部） ==");
            builder.AppendLine(ReadTailText(LatestLogPath, 300));
        }

        if (File.Exists(DebugLogPath))
        {
            builder.AppendLine();
            builder.AppendLine("== debug.log（尾部） ==");
            builder.AppendLine(ReadTailText(DebugLogPath, 600));
        }

        if (File.Exists(StderrLogPath))
        {
            builder.AppendLine();
            builder.AppendLine("== stderr（尾部） ==");
            builder.AppendLine(ReadTailText(StderrLogPath, 120));
        }

        File.WriteAllText(CrashLogPath, builder.ToString(), Utf8Bom);
        return CrashLogPath;
    }

    private string? FindNewestCrashReport()
    {
        try
        {
            if (!Directory.Exists(CrashReportDirectory))
            {
                return null;
            }

            return Directory.EnumerateFiles(CrashReportDirectory, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadTailText(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return string.Join('\n', lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void PruneOldSessions()
    {
        try
        {
            var baseDirectory = Path.Combine(_launcherRoot, "logs", "launches");
            if (!Directory.Exists(baseDirectory))
            {
                return;
            }

            var sessions = Directory.EnumerateDirectories(baseDirectory)
                .Where(path => !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(DirectoryPath),
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Path = path,
                    IsNormal = IsNormalSession(path),
                    LastWrite = Directory.GetLastWriteTimeUtc(path),
                })
                .ToList();

            foreach (var stale in sessions
                         .Where(item => item.IsNormal)
                         .OrderByDescending(item => item.LastWrite)
                         .Skip(10))
            {
                TryDeleteDirectory(stale.Path);
            }

            foreach (var stale in sessions
                         .Where(item => !item.IsNormal)
                         .OrderByDescending(item => item.LastWrite)
                         .Skip(50))
            {
                TryDeleteDirectory(stale.Path);
            }
        }
        catch
        {
        }
    }

    private static bool IsNormalSession(string directory)
    {
        try
        {
            var result = Path.Combine(directory, "result.json");
            if (!File.Exists(result))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(result));
            return document.RootElement.TryGetProperty("exit_code", out var code) &&
                   code.TryGetInt32(out var value) &&
                   value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    public string ExportZip()
    {
        var exportDirectory = Path.Combine(_launcherRoot, "logs", "exports");
        Directory.CreateDirectory(exportDirectory);
        var zipPath = Path.Combine(exportDirectory, Path.GetFileName(DirectoryPath) + ".zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(DirectoryPath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zipPath;
    }

    private void CopyIfExists(string source, string name)
    {
        if (!File.Exists(source))
        {
            return;
        }

        File.Copy(source, Path.Combine(DirectoryPath, name), overwrite: true);
    }

    private void CopyDirectoryIfExists(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            catch
            {
            }
        }
    }

    private void WriteJson(string name, object value)
    {
        File.WriteAllText(
            Path.Combine(DirectoryPath, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return text.Length > 40 ? text[..40] : text;
    }
}
