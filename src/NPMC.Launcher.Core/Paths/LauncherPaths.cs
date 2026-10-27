namespace NPMC.Launcher.Core.Paths;

/// <summary>
/// 启动器的全部磁盘路径，一律由根目录推导，不落盘任何绝对路径。
///
/// 这是"整个文件夹可以自由迁移"的基础：
///   - 根目录取自 AppContext.BaseDirectory（单文件发布下也指向 exe 所在目录）
///     注意：不要用 Assembly.Location，单文件发布时它返回空字符串。
///   - 配置里只存相对路径或运行时组件名，不存绝对路径。
///   - 启动进程时 WorkingDirectory 同样从根目录推导。
///
/// 两个根的区别：
///   - <see cref="Root"/>：启动器自身所在目录（runtime、instances 固定在这里）
///   - <see cref="SharedRoot"/>：versions / libraries / assets 的根。
///     默认等于 Root；"完全隔离"的实例会把它指向实例自己的目录，
///     这样该实例的库与资源也独立，整个实例可以被单独拷走。
/// </summary>
public sealed class LauncherPaths
{
    public string Root { get; }

    /// <summary>versions / libraries / assets 的根目录。</summary>
    public string SharedRoot { get; }

    /// <summary>游戏文件根目录（实例、runtime、默认游戏目录）；与启动器配置分离。</summary>
    public string GameRoot { get; }

    public LauncherPaths(string? root = null, string? sharedRoot = null, string? gameRoot = null)
    {
        Root = Normalize(root ?? AppContext.BaseDirectory);
        SharedRoot = string.IsNullOrWhiteSpace(sharedRoot) ? Root : Normalize(sharedRoot);
        GameRoot = string.IsNullOrWhiteSpace(gameRoot) ? SharedRoot : Normalize(gameRoot);
    }

    /// <summary>
    /// 便携布局：&lt;base&gt;\data 存启动器配置，&lt;base&gt;\minecraft 存游戏文件。
    ///
    /// 出于数据安全，**不自动迁移**已有旧布局：检测到 &lt;base&gt; 根目录还有
    /// settings.json / libraries / versions / instances 等旧文件时，继续按旧根目录工作。
    /// 只有全新目录才启用 data + minecraft。要迁移请单独执行备份/迁移流程。
    /// </summary>
    public static LauncherPaths Portable(string baseDirectory)
    {
        var root = Normalize(baseDirectory);

        if (HasLegacyLayout(root))
            return new LauncherPaths(root);

        var data = Path.Combine(root, "data");
        var minecraft = Path.Combine(root, "minecraft");

        Directory.CreateDirectory(data);
        Directory.CreateDirectory(minecraft);

        return new LauncherPaths(data, sharedRoot: minecraft, gameRoot: minecraft);
    }

    private static bool HasLegacyLayout(string root)
    {
        foreach (var name in new[] { "settings.json", "instances.json", "libraries",
                                     "versions", "assets", "instances", "runtime" })
        {
            if (File.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(root, name)))
                return true;
        }

        return false;
    }

    private static void MigrateLegacyLayout(string root, string data, string minecraft)
    {
        // 旧版把配置和游戏文件都散在根目录；只移动明确属于这两类的条目，不碰其它用户文件。
        foreach (var name in new[] { "libraries", "versions", "assets", "runtime", "instances" })
            TryMove(Path.Combine(root, name), Path.Combine(minecraft, name), directory: true);

        TryMove(Path.Combine(root, "launcher_profiles.json"),
            Path.Combine(minecraft, "launcher_profiles.json"), directory: false);

        foreach (var name in new[] { "settings.json", "accounts.json", "instances.json",
                                     "microsoft-tokens", "cache", "logs" })
            TryMove(Path.Combine(root, name), Path.Combine(data, name),
                directory: name is "microsoft-tokens" or "cache" or "logs");
    }

    private static void TryMove(string source, string destination, bool directory)
    {
        try
        {
            if (!directory)
            {
                if (!File.Exists(source) || File.Exists(destination)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
                return;
            }

            if (!Directory.Exists(source) || Directory.Exists(destination)) return;
            Directory.Move(source, destination);
        }
        catch
        {
            // 迁移失败不阻断启动；保留旧目录，用户仍可手动处理
        }
    }

    // ---- 固定属于游戏目录 ----
    public string Runtime   => Path.Combine(GameRoot, "runtime");
    public string Instances => Path.Combine(GameRoot, "instances");

    // ---- 可被实例隔离的目录 ----
    public string Libraries => Path.Combine(SharedRoot, "libraries");
    public string Versions  => Path.Combine(SharedRoot, "versions");
    public string Assets    => Path.Combine(SharedRoot, "assets");

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
