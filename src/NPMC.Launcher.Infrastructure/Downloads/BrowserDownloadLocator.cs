using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NPMC.Launcher.Infrastructure.Downloads;

/// <summary>
/// 找“默认浏览器”实际使用的下载目录 + 系统 Downloads 兜底。
///
/// 按钮是用系统默认浏览器打开的，所以只需要看默认浏览器的配置：
///   - Chrome / Edge / Brave / Vivaldi / Opera：User Data 下的 Preferences 里
///     download.default_directory；
///   - Firefox：prefs.js 的 browser.download.dir（folderList=2 才是自定义）。
/// 识别不到就只用系统 Downloads。
/// </summary>
public static class BrowserDownloadLocator
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static IReadOnlyList<string> GetCandidateDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Directory.Exists(path)) return;
                if (!seen.Add(Path.GetFullPath(path))) return;
                directories.Add(path);
            }
            catch
            {
                // 路径不合法就跳过
            }
        }

        Add(GetDefaultBrowserDownloadDirectory());
        Add(GetKnownDownloadsFolder());

        return directories;
    }

    public static string GetKnownDownloadsFolder()
    {
        try
        {
            var id = DownloadsFolderId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) == 0
                && pointer != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
        catch
        {
            // 退回环境变量
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>默认浏览器的下载目录；识别不到返回 null。</summary>
    public static string? GetDefaultBrowserDownloadDirectory()
    {
        var progId = ReadDefaultBrowserProgId();
        if (string.IsNullOrWhiteSpace(progId)) return null;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
            return ReadFirefoxDownloadDirectory(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"));

        if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            return ReadChromiumDownloadDirectory(Path.Combine(local, "Google", "Chrome", "User Data"));

        if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            return ReadChromiumDownloadDirectory(Path.Combine(local, "Microsoft", "Edge", "User Data"));

        if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase))
            return ReadChromiumDownloadDirectory(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));

        if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase))
            return ReadChromiumDownloadDirectory(Path.Combine(local, "Vivaldi", "User Data"));

        if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase))
            return ReadChromiumDownloadDirectory(Path.Combine(roaming, "Opera Software", "Opera Stable"))
                   ?? ReadChromiumDownloadDirectory(Path.Combine(local, "Opera Software", "Opera Stable"));

        return null;
    }

    private static string? ReadDefaultBrowserProgId()
    {
        try
        {
            const string key =
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";

            using var registry = Registry.CurrentUser.OpenSubKey(key);
            return registry?.GetValue("ProgId") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadChromiumDownloadDirectory(string userDataDirectory)
    {
        if (!Directory.Exists(userDataDirectory)) return null;

        // Default 优先，其次任意 Profile *
        var candidates = new List<string> { Path.Combine(userDataDirectory, "Preferences") };

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(userDataDirectory))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(Path.Combine(directory, "Preferences"));
            }
        }
        catch
        {
            // ignore
        }

        foreach (var preferences in candidates)
        {
            var path = ReadChromiumPreferences(preferences);
            if (path is not null) return path;
        }

        return null;
    }

    private static string? ReadChromiumPreferences(string preferencesPath)
    {
        if (!File.Exists(preferencesPath)) return null;

        try
        {
            using var stream = File.OpenRead(preferencesPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("download", out var download)) return null;
            if (!download.TryGetProperty("default_directory", out var directory)) return null;

            return directory.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFirefoxDownloadDirectory(string profilesDirectory)
    {
        if (!Directory.Exists(profilesDirectory)) return null;

        IEnumerable<string> profiles;

        try
        {
            profiles = Directory.EnumerateDirectories(profilesDirectory).ToList();
        }
        catch
        {
            return null;
        }

        foreach (var profile in profiles)
        {
            var prefs = Path.Combine(profile, "prefs.js");
            if (!File.Exists(prefs)) continue;

            string text;

            try
            {
                text = File.ReadAllText(prefs);
            }
            catch
            {
                continue;
            }

            if (!Regex.IsMatch(text, @"browser\.download\.folderList""\s*,\s*2"))
                continue;

            var match = Regex.Match(
                text, @"browser\.download\.dir""\s*,\s*""((?:[^""\\]|\\.)*)""");

            if (match.Success) return Regex.Unescape(match.Groups[1].Value);
        }

        return null;
    }
}
