using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 实例在磁盘上的布局。
///
/// 设计要点：
///   - <see cref="GameDirectory"/> 就是传给 --gameDir 的目录
///   - <see cref="SharedRoot"/> 决定 versions / libraries / assets 落在启动器根目录还是实例目录
///   - <see cref="OwnsGameDirectory"/> 用来判断"能不能安全删除这个目录"：
///     无隔离时游戏目录就是根目录，绝不能删
/// </summary>
public sealed class InstanceLayout
{
    public LauncherPaths Launcher { get; }
    public LauncherInstance Instance { get; }

    public InstanceLayout(LauncherPaths launcher, LauncherInstance instance)
    {
        Launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    /// <summary>传给 --gameDir 的目录。</summary>
    public string GameDirectory => Instance.Isolation == IsolationLevel.None
        ? Launcher.Root
        : Path.Combine(Launcher.Instances, Instance.Id);

    /// <summary>versions / libraries / assets 的根目录。</summary>
    public string SharedRoot => Instance.Isolation == IsolationLevel.Full
        ? GameDirectory
        : Launcher.Root;

    /// <summary>带 SharedRoot 的路径集合，可直接交给安装器/启动器使用。</summary>
    public LauncherPaths Paths => new(Launcher.Root, SharedRoot);

    // ---- 游戏目录下的标准子目录 ----
    public string Mods           => Path.Combine(GameDirectory, "mods");
    public string ResourcePacks  => Path.Combine(GameDirectory, "resourcepacks");
    public string ShaderPacks    => Path.Combine(GameDirectory, "shaderpacks");
    public string Saves          => Path.Combine(GameDirectory, "saves");
    public string Config         => Path.Combine(GameDirectory, "config");
    public string Logs           => Path.Combine(GameDirectory, "logs");
    public string CrashReports   => Path.Combine(GameDirectory, "crash-reports");

    public string LatestLog => Path.Combine(Logs, "latest.log");

    /// <summary>启动器首次创建实例时会建好的目录。</summary>
    public IReadOnlyList<string> StableDirectories => new[]
    {
        Mods, ResourcePacks, ShaderPacks, Saves, Config,
    };

    /// <summary>该实例是否独占自己的游戏目录（无隔离时为 false，因为那是启动器根目录）。</summary>
    public bool OwnsGameDirectory => Instance.Isolation != IsolationLevel.None;

    public void EnsureDirectories()
    {
        if (OwnsGameDirectory) Directory.CreateDirectory(GameDirectory);
        foreach (var dir in StableDirectories) Directory.CreateDirectory(dir);
    }

    /// <summary>列出某个子目录下的文件，用于「打开 mods / 资源包 / 光影文件夹」这类界面。</summary>
    public IReadOnlyList<string> ListFiles(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
