using System.Diagnostics;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Skins;

namespace NPMC.Launcher.Infrastructure.Launch;

/// <summary>启动前的准备结果。Problems 非空表示不能启动，原因逐条列出。</summary>
public sealed record PreparedLaunch
{
    public required LaunchPlan Plan { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>不影响启动、但值得记一笔的环境修正（例如提升了 Java 的完整性标签）。</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>游戏日志路径（&lt;gameDir&gt;\logs\latest.log），供界面 tail。</summary>
    public string? LatestLog { get; init; }

    public string GameDirectory => Plan.WorkingDirectory;

    public bool CanLaunch => Problems.Count == 0;
}

/// <summary>
/// 组装启动计划并启动 java。界面与 CLI 共用这一份逻辑，避免两边行为漂移。
///
/// 关于日志：**不要依赖进程 stdout**。客户端配置的 log4j（client-1.12.xml）会把
/// 日志按 XML 格式写到 stdout，人读不了；真正的可读日志在 &lt;gameDir&gt;\logs\latest.log。
/// 因此这里的 Start 不重定向标准输出 —— 既避免管道缓冲区写满造成 java 卡死，
/// 也让界面去 tail latest.log。
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// 读取本地已安装的版本 JSON，**并把 inheritsFrom 链合并掉**。启动不应依赖网络：
    /// 版本 JSON 在 install 阶段就已落盘，直接读缓存即可。
    /// </summary>
    public static async Task<VersionJson?> TryLoadInstalledVersionAsync(
        LauncherPaths paths, string versionId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadWithInheritanceAsync(paths, versionId, cancellationToken).ConfigureAwait(false);
        return loaded?.Version;
    }

    /// <summary>
    /// 实例的**游戏版本号**：沿 inheritsFrom 走到链尾。
    ///
    /// 为什么需要：加载器 profile 的 id 是 <c>fabric-loader-0.20.1-1.20.1</c>，
    /// 拿它去问 Modrinth / CurseForge 的 game_versions 会一条都搜不到 ——
    /// 那边只认 <c>1.20.1</c>。
    /// </summary>
    public static async Task<string> ResolveGameVersionAsync(
        LauncherPaths paths, string versionId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadWithInheritanceAsync(paths, versionId, cancellationToken).ConfigureAwait(false);
        return loaded?.RootId ?? versionId;
    }

    private static async Task<(VersionJson Version, string RootId)?> LoadWithInheritanceAsync(
        LauncherPaths paths,
        string versionId,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        // 防止 JSON 写错成环
        if (depth > 8) return null;

        var raw = await TryLoadRawAsync(paths, versionId, cancellationToken).ConfigureAwait(false);
        if (raw is null) return null;

        if (raw.InheritsFrom is not { } parentId)
            return (raw with { RootVersionId = raw.Id }, raw.Id);

        var parent = await LoadWithInheritanceAsync(paths, parentId, cancellationToken, depth + 1)
            .ConfigureAwait(false);

        // 父版本没装：原样返回，让启动检查报"缺少 client jar"而不是给出误导性的成功
        if (parent is null) return (raw, raw.Id);

        var merged = VersionInheritance.Merge(raw, parent.Value.Version)
                     with { RootVersionId = parent.Value.RootId };

        return (merged, parent.Value.RootId);
    }

    private static async Task<VersionJson?> TryLoadRawAsync(
        LauncherPaths paths, string versionId, CancellationToken cancellationToken)
    {
        var jsonPath = paths.VersionJson(versionId);
        if (!File.Exists(jsonPath)) return null;

        var json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
        return VersionJsonSerializer.Parse(json);
    }

    /// <summary>组装启动计划，并检查 java 与 client jar 是否就位。不启动任何进程。</summary>
    public static PreparedLaunch Prepare(
        LauncherPaths launcher,
        LauncherInstance? instance,
        VersionJson version,
        LauncherSettings launcherSettings,
        PlayerAccount? account = null,
        string? accessToken = null)
    {
        var layout = instance is null ? null : new InstanceLayout(launcher, instance);
        layout?.EnsureDirectories();

        var paths = layout?.Paths ?? launcher;
        var instanceSettings = instance?.Settings ?? new InstanceSettings();
        var os = OsInfo.Current();
        var logConfig = version.Logging?.Client?.File is { } file ? paths.LogConfigFile(file.Id) : null;

        LaunchContext baseContext;

        if (account is null)
        {
            baseContext = LaunchContext.OfflineProbe("Player");
        }
        else if (account.Kind == AccountKind.Microsoft)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException(
                    $"微软账号「{account.Username}」缺少 access token，请重新登录");

            baseContext = LaunchContext.Online(account.Username, account.Uuid, accessToken);
        }
        else
        {
            baseContext = LaunchContext.Offline(account.Username, account.Uuid);
        }

        var context = baseContext with
        {
            Width = instanceSettings.WindowWidth,
            Height = instanceSettings.WindowHeight,
        };

        var memory = instanceSettings.MemoryMb > 0
            ? instanceSettings.MemoryMb
            : launcherSettings.DefaultMemoryMb;

        // client.jar、natives 与资源索引都在**根版本**目录下。
        // 加载器 profile 自己的目录里只有那个 JSON，没有 jar —— 用它去找必然报"缺少 client jar"。
        var rootVersionId = version.RootVersionId ?? version.Id;

        var plan = LaunchPlanFactory.Create(paths, rootVersionId, version, context, os,
            memoryMb: memory,
            hasCustomResolution: !instanceSettings.Fullscreen,
            logConfigFile: logConfig,
            gameDirectory: layout?.GameDirectory);

        // 离线皮肤：账号选了皮肤（自定义 / 同名正版）且该版本 authlib 支持时，挂上 javaagent。
        // 只在 JVM 参数里加一项，不动实例目录、不装模组。
        var skinAgent = SkinAgentProvider.TryCreateLaunch(
            launcher, version, account, paths.ClientJar(rootVersionId));
        var skinAgentArgument = skinAgent?.Argument;

        // 1.7.10 + SkinPort：本地 cachedImages + agent 的 /skins 端点。
        // 本地账号走本地皮肤；其它 UUID 由 SkinPort 继续问 Mojang/Crafatar。
        if (layout is not null)
            SkinPortProvisioner.Provision(layout.GameDirectory, account, skinAgent?.Port);

        if (skinAgentArgument is not null)
            plan = plan with
            {
                JvmArguments = new[] { skinAgentArgument }.Concat(plan.JvmArguments).ToList(),
            };

        // 游戏进程的临时目录：不能让它用 %TEMP%。
        // 启动器/Java 可能是 Low 完整性（java.exe 自带 Low 标签），而 %TEMP% 是 Medium，
        // Low 进程在 %TEMP% 建目录会被拒绝（EuphoriaPatcher 的
        // "Error creating temporary directory" 就是这个原因）。这里给一个位于数据目录、
        // 与启动器同级（Low）的临时目录，并显式传给 JVM。
        var gameTemp = paths.GameTemp;
        try
        {
            Directory.CreateDirectory(gameTemp);

            // 明确标成 Low：java.exe 常带 Low 标签，进程会降到 Low；
            // Low 只能写同级或更低的目标，标成 Low 才保证它一定写得进去。
            IntegrityGuard.TryRaiseFileLevel(gameTemp, WindowsIntegrityLevel.Low, out _);
        }
        catch
        {
            // 建不出来就退回默认，至少不挡住启动
            gameTemp = "";
        }

        if (!string.IsNullOrEmpty(gameTemp))
            plan = plan with { JvmArguments = new[] { $"-Djava.io.tmpdir={gameTemp}" }.Concat(plan.JvmArguments).ToList() };

        // Java 优先级：实例指定路径 → 实例指定组件 → 全局默认 Java → 版本元数据声明的组件
        var javaPath = plan.JavaPath;
        if (!string.IsNullOrWhiteSpace(instanceSettings.JavaPath))
            javaPath = instanceSettings.JavaPath!;
        else if (!string.IsNullOrWhiteSpace(instanceSettings.JavaComponent))
            javaPath = paths.JavaExe(instanceSettings.JavaComponent!);
        else if (!string.IsNullOrWhiteSpace(launcherSettings.DefaultJavaPath))
            javaPath = launcherSettings.DefaultJavaPath!;

        if (!string.Equals(javaPath, plan.JavaPath, StringComparison.OrdinalIgnoreCase))
            plan = plan with { JavaPath = javaPath };

        if (instanceSettings.ExtraJvmArguments.Count > 0)
            plan = plan with { JvmArguments = plan.JvmArguments.Concat(instanceSettings.ExtraJvmArguments).ToList() };

        var problems = new List<string>();
        var notices = new List<string>();

        if (!File.Exists(plan.JavaPath))
        {
            problems.Add($"缺少 Java 运行时：{plan.JavaPath}（可运行 npmc runtime {version.JavaVersion?.Component ?? "java-runtime-gamma"}）");
        }
        else
        {
            // 工作区被标成低完整性时，.data/runtime 下的 java.exe 会继承 Low 标签，
            // Windows 于是让 JVM 以 Low 运行；JDK 的 Files.isWritable 在 Low 进程里
            // 对所有文件都返回 false，Fabric 的 zipfs 因此把重映射产物当成只读。
            // 实测见 docs/architecture.md「Fabric 重映射失败」一节。
            var integrity = IntegrityGuard.EnsureExecutableRunsAtLauncherLevel(plan.JavaPath);

            if (integrity.Repaired)
            {
                notices.Add(
                    $"已把 Java 运行时的完整性标签从 {integrity.ExecutableLevel} 提升到 {integrity.RaisedTo}" +
                    $"（否则 Fabric 会把重映射产物判为只读）：{plan.JavaPath}");
            }
            else if (integrity.Problem is { } integrityProblem)
            {
                // Fabric/Quilt 的重映射一定会失败，提前给出可操作的原因，别让用户对着 tinyremapper 的栈发愣
                if (UsesJarRemapping(version))
                    problems.Add(integrityProblem);
                else
                    notices.Add(integrityProblem);
            }
        }

        var clientJar = paths.ClientJar(rootVersionId);
        if (!File.Exists(clientJar))
            problems.Add($"缺少 client jar：{clientJar}（可运行 npmc install {rootVersionId}）");

        Directory.CreateDirectory(paths.NativesDir(rootVersionId));

        // LWJGL 3.4 用 <build>/<arch>/ 子目录缓存本机库；同目录里若还留着 3.3 时代的平铺 dll，
        // 它的平台探测会认错架构（实测 26.3 报 windows/arm64 后崩在 FML 早期窗口）。
        var cleanedNatives = NativeLibraryCache.RemoveStaleFlatFiles(version, plan.JvmArguments);
        if (cleanedNatives > 0)
            notices.Add($"已清理 {cleanedNatives} 个旧布局的本机库缓存文件（否则 LWJGL 会把架构认错）");

        return new PreparedLaunch
        {
            Plan = plan,
            Problems = problems,
            Notices = notices,
            LatestLog = layout?.LatestLog,
        };
    }

    /// <summary>
    /// 这个版本是否由“会重映射游戏 jar”的加载器启动。
    /// Fabric 与 Quilt 都走 Knot → tinyremapper；原版与 Forge 不经过这条路径。
    /// </summary>
    private static bool UsesJarRemapping(VersionJson version)
        => version.MainClass.Contains("knot", StringComparison.OrdinalIgnoreCase)
           || version.Id.Contains("fabric", StringComparison.OrdinalIgnoreCase)
           || version.Id.Contains("quilt", StringComparison.OrdinalIgnoreCase);

    /// <summary>启动 java。调用方负责等待退出（<c>WaitForExitAsync</c>）与处置进程对象。</summary>
    public static Process Start(PreparedLaunch prepared)
    {
        if (!prepared.CanLaunch)
            throw new InvalidOperationException(string.Join("；", prepared.Problems));

        // 同一实例同时只允许一个游戏进程。
        // 第二个进程会拿不到 session.lock，行为是"退出码 1 且不写日志"，
        // 用户只能看到一句无从下手的错误 —— 所以在启动前就拦下来。
        var lockPath = PrepareInstanceLock(prepared.Plan.WorkingDirectory);

        // Fabric 会在 <gameDir>\.fabric\remappedJars 里做一次性重映射。
        // 如果上一次是被强制结束的（任务管理器、崩溃、启动器关闭），
        // 会留下半个 *.tmp，而 Fabric 下次写入这个同名 tmp 时会直接失败，
        // 报 "the jar file ...client-intermediary.jar.tmp can't be written"。
        // 这些文件随时可以重建，启动前清掉最稳妥。
        CleanRemapScratch(prepared.Plan.WorkingDirectory);

        var startInfo = new ProcessStartInfo(prepared.Plan.JavaPath)
        {
            // 工作目录必须是 gameDir：客户端的 log4j 配置用相对路径 logs/latest.log
            // （见 assets/log_configs/client-*.xml），不设它日志会落到启动器自己的 CWD，
            // 实例目录里的 latest.log 永远是旧的。
            WorkingDirectory = prepared.Plan.WorkingDirectory,

            // java 是控制台程序，默认会开一个黑色控制台窗口。
            // 设了 CreateNoWindow 就不会弹窗；stdout/stderr 仍然重定向（见下）。
            CreateNoWindow = true,

            // 但只设 CreateNoWindow 不读管道是危险的：
            // 客户端 log4j 会往 stdout 写大量日志，一旦管道缓冲区写满，
            // java 就会**阻塞在写操作上**，表现是游戏卡死。
            // 所以必须重定向并把内容读空（下面用空处理器丢弃）。
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in prepared.Plan.ToProcessArguments())
            startInfo.ArgumentList.Add(argument);

        Process process;

        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 java 进程");
        }
        catch
        {
            ReleaseInstanceLock(lockPath);
            throw;
        }

        // 游戏退出后释放锁；异常退出也不会留下永久锁（PID 检查会兜底）
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ReleaseInstanceLock(lockPath);

        // 早期失败（JVM 起不来、main class 找不到等）**不会**写 latest.log，
        // 唯一的信息就在 stderr 里。所以这里不能丢：写到实例 logs 下供失败后查看，
        // 同时持续把管道读空，避免缓冲区写满导致游戏卡死。
        // 把这次真正执行的命令行记下来：出问题时要能和"手动跑一次"逐字对比
        try
        {
            Directory.CreateDirectory(Path.Combine(prepared.Plan.WorkingDirectory, "logs"));
            File.WriteAllText(
                Path.Combine(prepared.Plan.WorkingDirectory, "logs", "nmpc-command.txt"),
                prepared.Plan.ToDisplayString());
        }
        catch
        {
            // 记不下来不影响启动
        }

        var stdoutPath = StdoutLogPath(prepared.Plan.WorkingDirectory);
        var stderrPath = StderrLogPath(prepared.Plan.WorkingDirectory);

        TryDelete(stdoutPath);
        TryDelete(stderrPath);

        var stdoutWriter = TryCreateWriter(stdoutPath);
        var stderrWriter = TryCreateWriter(stderrPath);

        process.OutputDataReceived += (_, line) => WriteLine(stdoutWriter, line.Data);
        process.ErrorDataReceived += (_, line) => WriteLine(stderrWriter, line.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.Exited += (_, _) =>
        {
            CloseQuietly(stdoutWriter);
            CloseQuietly(stderrWriter);
        };

        return process;
    }
    /// <summary>清掉 Fabric 重映射留下的半成品文件（只删 *.tmp）。</summary>
    private static void CleanRemapScratch(string gameDirectory)
    {
        try
        {
            var remapRoot = Path.Combine(gameDirectory, ".fabric", "remappedJars");
            if (!Directory.Exists(remapRoot)) return;

            // 只要发现半成品，就把整个缓存目录重建：
            // 崩溃时可能连已完成的 jar 都是截断的，留着反而更难查。
            var hasScratch = Directory
                .EnumerateFiles(remapRoot, "*.tmp", SearchOption.AllDirectories)
                .Any();

            if (!hasScratch) return;

            try { Directory.Delete(remapRoot, recursive: true); } catch { }
        }
        catch
        {
            // 清理失败不影响启动，Fabric 自己会报错
        }
    }

    /// <summary>游戏标准输出的落盘位置（每次启动覆盖）。</summary>
    public static string StdoutLogPath(string gameDirectory)
        => Path.Combine(gameDirectory, "logs", "nmpc-stdout.log");

    /// <summary>游戏标准错误的落盘位置（每次启动覆盖）。</summary>
    public static string StderrLogPath(string gameDirectory)
        => Path.Combine(gameDirectory, "logs", "nmpc-stderr.log");

    /// <summary>读 stderr 日志的最后若干行（启动失败时给用户看）。</summary>
    public static string ReadStderrTail(string gameDirectory, int maxChars = 600)
    {
        var path = StderrLogPath(gameDirectory);

        try
        {
            if (!File.Exists(path)) return "";

            var text = File.ReadAllText(path).Trim();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch
        {
            return "";
        }
    }

    private static StreamWriter? TryCreateWriter(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLine(StreamWriter? writer, string? line)
    {
        if (writer is null || line is null) return;

        try { writer.WriteLine(line); } catch { }
    }

    private static void CloseQuietly(StreamWriter? writer)
    {
        try { writer?.Dispose(); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// 在实例目录里建一个运行锁（含启动器 PID），并返回锁文件路径。
    ///
    /// 已存在且持有者仍在运行时抛出 <see cref="InvalidOperationException"/>，
    /// 让调用方给出"该实例已在运行"这种可理解的提示；
    /// 持有者已消失（上次异常退出）则视为残留锁，直接接管。
    /// </summary>
    private static string PrepareInstanceLock(string gameDirectory)
    {
        var lockPath = Path.Combine(gameDirectory, ".nmpc-running");

        try
        {
            Directory.CreateDirectory(gameDirectory);

            if (File.Exists(lockPath))
            {
                if (IsLockHolderAlive(lockPath))
                    throw new InvalidOperationException(
                        "该实例已经在运行了（同一实例不能同时启动两次）。请先关闭正在运行的游戏窗口。");

                // 残留锁：上次异常退出留下的，直接接管
                File.Delete(lockPath);
            }

            File.WriteAllText(lockPath, Environment.ProcessId.ToString());
            return lockPath;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // 锁只是辅助手段，建不了也不该阻止启动
            return "";
        }
    }

    /// <summary>锁文件里记录的进程是否还活着。</summary>
    private static bool IsLockHolderAlive(string lockPath)
    {
        try
        {
            var text = File.ReadAllText(lockPath).Trim();
            if (!int.TryParse(text, out var pid)) return false;

            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            // 进程不存在（或无权查询）→ 当作残留锁
            return false;
        }
    }

    private static void ReleaseInstanceLock(string lockPath)
    {
        if (string.IsNullOrEmpty(lockPath)) return;

        try
        {
            if (File.Exists(lockPath)) File.Delete(lockPath);
        }
        catch
        {
            // 删不掉就留给下次的 PID 检查处理
        }
    }
}
