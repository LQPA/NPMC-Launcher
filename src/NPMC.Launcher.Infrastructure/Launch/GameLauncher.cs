using System.Diagnostics;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Core.Settings;

namespace NPMC.Launcher.Infrastructure.Launch;

/// <summary>启动前的准备结果。Problems 非空表示不能启动，原因逐条列出。</summary>
public sealed record PreparedLaunch
{
    public required LaunchPlan Plan { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }

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
        LauncherSettings launcherSettings)
    {
        var layout = instance is null ? null : new InstanceLayout(launcher, instance);
        layout?.EnsureDirectories();

        var paths = layout?.Paths ?? launcher;
        var instanceSettings = instance?.Settings ?? new InstanceSettings();
        var os = OsInfo.Windows64();
        var logConfig = version.Logging?.Client?.File is { } file ? paths.LogConfigFile(file.Id) : null;

        var context = LaunchContext.OfflineProbe() with
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

        // Java 优先级：实例指定路径 → 实例指定组件 → 版本元数据声明的组件
        var javaPath = plan.JavaPath;
        if (!string.IsNullOrWhiteSpace(instanceSettings.JavaPath))
            javaPath = instanceSettings.JavaPath!;
        else if (!string.IsNullOrWhiteSpace(instanceSettings.JavaComponent))
            javaPath = paths.JavaExe(instanceSettings.JavaComponent!);

        if (!string.Equals(javaPath, plan.JavaPath, StringComparison.OrdinalIgnoreCase))
            plan = plan with { JavaPath = javaPath };

        if (instanceSettings.ExtraJvmArguments.Count > 0)
            plan = plan with { JvmArguments = plan.JvmArguments.Concat(instanceSettings.ExtraJvmArguments).ToList() };

        var problems = new List<string>();

        if (!File.Exists(plan.JavaPath))
            problems.Add($"缺少 Java 运行时：{plan.JavaPath}（可运行 npmc runtime {version.JavaVersion?.Component ?? "java-runtime-gamma"}）");

        var clientJar = paths.ClientJar(rootVersionId);
        if (!File.Exists(clientJar))
            problems.Add($"缺少 client jar：{clientJar}（可运行 npmc install {rootVersionId}）");

        Directory.CreateDirectory(paths.NativesDir(rootVersionId));

        return new PreparedLaunch
        {
            Plan = plan,
            Problems = problems,
            LatestLog = layout?.LatestLog,
        };
    }

    /// <summary>启动 java。调用方负责等待退出（<c>WaitForExitAsync</c>）与处置进程对象。</summary>
    public static Process Start(PreparedLaunch prepared)
    {
        if (!prepared.CanLaunch)
            throw new InvalidOperationException(string.Join("；", prepared.Problems));

        // 同一实例同时只允许一个游戏进程。
        // 第二个进程会拿不到 session.lock，行为是"退出码 1 且不写日志"，
        // 用户只能看到一句无从下手的错误 —— 所以在启动前就拦下来。
        var lockPath = PrepareInstanceLock(prepared.Plan.WorkingDirectory);

        var startInfo = new ProcessStartInfo(prepared.Plan.JavaPath)
        {
            WorkingDirectory = prepared.Plan.WorkingDirectory,
            UseShellExecute = false,

            // java 是控制台程序，默认会开一个黑色控制台窗口。
            // CREATE_NO_WINDOW 让它在没有控制台的情况下运行。
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