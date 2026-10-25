namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 实例的隔离级别。
///
/// 三级隔离的差别只在于"哪些目录是共享的"：
///   无隔离   → 游戏目录就是启动器根目录，所有实例共用 mods / 存档 / 配置
///   版本隔离 → 每个实例有独立游戏目录，但共享 versions / libraries / assets（默认）
///   完全隔离 → 游戏目录独立，且 versions / libraries / assets 也各自独立
/// </summary>
public enum IsolationLevel
{
    /// <summary>无隔离。</summary>
    None = 0,

    /// <summary>
    /// 版本隔离（默认）。共享 versions / libraries / assets，避免每个实例重复下载数 GB；
    /// mods / 资源包 / 光影 / 存档 / 配置 各自独立。
    /// </summary>
    Version = 1,

    /// <summary>完全隔离。连 versions / libraries / assets 都在实例目录内，整个实例可单独拷走。</summary>
    Full = 2,
}

/// <summary>实例级设置，覆盖全局默认值。</summary>
public sealed record InstanceSettings
{
    /// <summary>最大堆内存（MB）。</summary>
    public int MemoryMb { get; init; } = 4096;

    public int WindowWidth { get; init; } = 1280;
    public int WindowHeight { get; init; } = 720;
    public bool Fullscreen { get; init; }

    /// <summary>覆盖运行时组件名，例如 java-runtime-delta。为空则用版本元数据里的 javaVersion.component。</summary>
    public string? JavaComponent { get; init; }

    /// <summary>覆盖 java.exe 的绝对路径。为空则用内置运行时。</summary>
    public string? JavaPath { get; init; }

    public List<string> ExtraJvmArguments { get; init; } = new();
}

/// <summary>
/// 一个游戏实例：版本 + 独立游戏目录 + 自己的设置。
/// Id 同时是实例文件夹名，因此必须是合法的 Windows 目录名。
/// </summary>
public sealed record LauncherInstance
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string VersionId { get; init; } = "";

    public IsolationLevel Isolation { get; init; } = IsolationLevel.Version;

    public InstanceSettings Settings { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static LauncherInstance Create(
        string id, string name, string versionId,
        IsolationLevel isolation = IsolationLevel.Version,
        InstanceSettings? settings = null) => new()
        {
            Id = id,
            Name = name,
            VersionId = versionId,
            Isolation = isolation,
            Settings = settings ?? new InstanceSettings(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
