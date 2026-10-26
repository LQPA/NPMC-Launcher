using System.Security.Cryptography;
using NPMC.Launcher.Core.Modpacks;

namespace NPMC.Launcher.Infrastructure.Modpacks;

/// <summary>
/// 监听下载目录，用 SHA1 认领作者禁止 API 下载的文件，并放进实例。
///
/// 只认哈希一致的文件：用户在 CurseForge 网页上点到的是作者钉的那一版，
/// 不靠文件名猜测，也不会把别的来源的同名包混进来。
/// </summary>
public sealed class BlockedModImporter : IDisposable
{
    private readonly string _instanceDirectory;
    private readonly Action<string>? _status;
    private readonly List<BlockedModFile> _pending;
    private readonly object _gate = new();

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _directories = new();
    private Timer? _debounce;
    private bool _disposed;

    public BlockedModImporter(
        IEnumerable<BlockedModFile> blocked,
        string instanceDirectory,
        Action<string>? status = null)
    {
        _pending = blocked.ToList();
        _instanceDirectory = instanceDirectory;
        _status = status;
    }

    /// <summary>还没有认领到的文件。</summary>
    public IReadOnlyList<BlockedModFile> Pending
    {
        get { lock (_gate) return _pending.ToList(); }
    }

    public IReadOnlyList<BlockedModFile> Imported { get; } = new List<BlockedModFile>();

    /// <summary>开始监听一个目录（可多次调用，累加监听）。</summary>
    public void Watch(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Watch(new[] { directory });
    }

    /// <summary>同时监听多个下载目录（系统 Downloads + 各浏览器自定义目录）。</summary>
    public void Watch(IEnumerable<string> directories)
    {
        if (_disposed) return;

        var added = 0;

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            if (_directories.Contains(directory, StringComparer.OrdinalIgnoreCase)) continue;

            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, _) => ScheduleScan();
            watcher.Changed += (_, _) => ScheduleScan();
            watcher.Renamed += (_, _) => ScheduleScan();

            _watchers.Add(watcher);
            _directories.Add(directory);
            added++;
        }

        if (added == 0) return;

        _debounce?.Dispose();
        _debounce = new Timer(_ => Scan(), null, TimeSpan.FromMilliseconds(1200), Timeout.InfiniteTimeSpan);

        _status?.Invoke($"正在监听 {_directories.Count} 个下载目录：{string.Join("；", _directories)}");
    }

    /// <summary>立刻扫一遍目录（用于用户手动点“重新检查”）。</summary>
    public void ScanNow()
    {
        if (_disposed) return;
        Scan();
    }

    private void ScheduleScan()
    {
        if (_disposed) return;
        _debounce?.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
    }

    private void Scan()
    {
        if (_disposed) return;

        List<BlockedModFile> pending;
        lock (_gate) pending = _pending.ToList();

        if (pending.Count == 0) return;

        foreach (var directory in _directories.ToList())
        {
            if (_disposed) return;
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (_disposed) return;
                ScanFile(file, pending);
            }
        }
    }

    private void ScanFile(string file, List<BlockedModFile> pending)
    {
        if (file.Contains(".part", StringComparison.OrdinalIgnoreCase)
            || file.Contains(".crdownload", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string hash;
        try
        {
            // 浏览器还在写：打不开就等下一轮，避免把半个文件算成哈希
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            hash = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return;
        }

        var match = pending.FirstOrDefault(item =>
            string.Equals(item.Sha1, hash, StringComparison.OrdinalIgnoreCase));

        if (match is null) return;

        try
        {
            var destination = Path.Combine(_instanceDirectory, match.TargetFolder, match.FileName);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);

            lock (_gate) _pending.Remove(match);
            ((List<BlockedModFile>)Imported).Add(match);

            _status?.Invoke($"已导入 {match.FileName}（SHA1 校验通过）");
        }
        catch (Exception ex)
        {
            _status?.Invoke($"导入 {match.FileName} 失败：{ex.GetBaseException().Message}");
        }
    }
    public void Dispose()
    {
        _disposed = true;

        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _directories.Clear();

        _debounce?.Dispose();
        _debounce = null;
    }
}
