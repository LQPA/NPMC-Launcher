namespace NPMC.Launcher.Core.Paths;

/// <summary>
/// 启动器的全部磁盘路径，一律由根目录推导，不落盘任何绝对路径。
///
/// 这是"整个文件夹可以自由迁移"的基础：
///   - 根目录取自 AppContext.BaseDirectory（单文件发布下也指向 exe 所在目录）
///     注意：不要用 Assembly.Location，单文件发布时它返回空字符串。
///   - 配置里只存相对路径或运行时组件名，不存绝对路径。
///   - 启动进程时 WorkingDirectory 同样从根目录推导。
/// </summary>
public sealed class LauncherPaths
{
    public string Root { get; }

    public LauncherPaths(string? root = null) =>
        Root = Normalize(root ?? AppContext.BaseDirectory);

    // ---- 一级目录 ----
    public string Runtime   => Path.Combine(Root, "runtime");
    public string Libraries => Path.Combine(Root, "libraries");
    public string Versions  => Path.Combine(Root, "versions");
    public string Assets    => Path.Combine(Root, "assets");
    public string Instances => Path.Combine(Root, "instances");

    // ---- assets ----
    public string AssetsIndexes => Path.Combine(Assets, "indexes");
    public string AssetsObjects => Path.Combine(Assets, "objects");
    public string LogConfigs    => Path.Combine(Assets, "log_configs");

    // ---- 版本目录 ----
    public string VersionDir(string versionId)  => Path.Combine(Versions, versionId);
    public string VersionJson(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");
    public string ClientJar(string versionId)   => Path.Combine(VersionDir(versionId), versionId + ".jar");
    public string NativesDir(string versionId)  => Path.Combine(VersionDir(versionId), "natives");

    // ---- 运行时（Java）----
    // 组件名直接来自版本 JSON 的 javaVersion.component，例如 java-runtime-gamma，
    // 所以不需要维护"游戏版本 → Java 版本"的映射表。
    public string RuntimeDir(string component) => Path.Combine(Runtime, component);
    public string JavaExe(string component)    => Path.Combine(RuntimeDir(component), "bin", "java.exe");

    // ---- 资源文件 ----
    public string AssetIndexFile(string indexId) => Path.Combine(AssetsIndexes, indexId + ".json");
    public string AssetObjectFile(string hash)   => Path.Combine(AssetsObjects, hash[..2], hash);
    public string LogConfigFile(string id)       => Path.Combine(LogConfigs, id);

    // ---- 库 ----
    /// <summary>把 maven 路径（a/b/c.jar）映射到本地文件路径。</summary>
    public string LibraryFile(string mavenPath) =>
        Path.Combine(Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
