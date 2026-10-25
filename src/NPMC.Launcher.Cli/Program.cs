using System.Diagnostics;
using System.Text;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Minecraft;
using NPMC.Launcher.Infrastructure.Runtime;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "probe"    => await ProbeAsync(args[1..]),
                "install"  => await InstallAsync(args[1..]),
                "runtime"  => await RuntimeAsync(args[1..]),
                "launch"   => await LaunchAsync(args[1..]),
                "instance" => await InstanceAsync(args[1..]),
                _          => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return 1;
        }
    }

    // ---------------------------------------------------------------- probe

    private static async Task<int> ProbeAsync(string[] args)
    {
        var versionId = Required(args);
        var root = ResolveRoot(args);
        var paths = new LauncherPaths(root);

        using var http = HttpFileDownloader.CreateClient();
        var downloader = new HttpFileDownloader(http);
        var source = new VersionManifestSource(downloader.GetStringAsync);

        var version = await LoadVersionAsync(source, downloader, paths, versionId);

        var context = LaunchContext.OfflineProbe();
        var os = OsInfo.Windows64();
        var plan = LaunchPlanFactory.Create(paths, versionId, version, context, os,
            memoryMb: OptInt(args, "--memory", 4096),
            hasCustomResolution: true,
            logConfigFile: version.Logging?.Client?.File is { } f ? paths.LogConfigFile(f.Id) : null);

        var effective = version.Libraries
            .Where(lib => RuleEvaluator.IsAllowed(lib.Rules, os, FeatureSet.ForLaunch(true)))
            .ToList();

        Section("探针结果");
        Info($"版本            {version.Id} ({version.Type})");
        Info($"mainClass       {version.MainClass}");
        Info($"java            {(version.JavaVersion is { } jv ? $"{jv.MajorVersion} / {jv.Component}" : "未声明")}");
        Info($"assetIndex      {version.AssetIndex?.Id ?? version.Assets ?? "-"}  对象数见安装阶段");
        Info($"libraries       {version.Libraries.Count} 总数 / {effective.Count} 生效");
        Info($"classpath       {plan.JvmArguments.Count} 个 JVM 参数中包含，条目数 {CountClasspath(plan)}");
        Info($"JVM 参数        {plan.JvmArguments.Count} 个");
        Info($"游戏参数        {plan.GameArguments.Count} 个");

        var unresolved = LaunchPlanFactory.FindUnresolvedPlaceholders(plan);
        if (unresolved.Count == 0) Ok("未解析占位符  无");
        else Error($"未解析占位符  {string.Join(", ", unresolved)}");

        Section("JVM 参数");
        foreach (var a in plan.JvmArguments) Dim(Truncate(a, 160));

        Section("游戏参数");
        foreach (var a in plan.GameArguments) Dim(a);

        var dump = Path.Combine(paths.Root, "last-command.txt");
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(dump, plan.ToDisplayString(), Encoding.UTF8);
        Section("命令行已写入");
        Info(dump);

        return unresolved.Count == 0 ? 0 : 2;
    }

    // -------------------------------------------------------------- install

    private static async Task<int> InstallAsync(string[] args)
    {
        var versionId = Required(args);
        var paths = new LauncherPaths(ResolveRoot(args));
        var withAssets = Has(args, "--with-assets");

        using var http = HttpFileDownloader.CreateClient();
        var downloader = new HttpFileDownloader(http);
        var source = new VersionManifestSource(downloader.GetStringAsync);

        var version = await LoadVersionAsync(source, downloader, paths, versionId);
        var os = OsInfo.Windows64();
        var effective = version.Libraries
            .Where(lib => RuleEvaluator.IsAllowed(lib.Rules, os, FeatureSet.ForLaunch(true)))
            .ToList();

        // 1) client jar
        if (version.Downloads?.Client is { } client)
        {
            Section("client.jar");
            await downloader.EnsureAsync(client.Url, paths.ClientJar(versionId), client.Sha1);
            Info($"{versionId}.jar  {Mb(client.Size)}");
        }

        // 2) libraries
        Section($"libraries ({effective.Count} 个)");
        var done = 0;
        foreach (var lib in effective)
        {
            var artifact = lib.Downloads?.Artifact;
            if (artifact is null || string.IsNullOrEmpty(artifact.Url))
            {
                Warn($"跳过（无 artifact）: {lib.Name}");
                continue;
            }
            await downloader.EnsureAsync(artifact.Url, paths.LibraryFile(artifact.Path), artifact.Sha1);
            done++;
            if (done % 20 == 0) Dim($"{done}/{effective.Count}");
        }
        Ok($"完成 {done} 个库");

        // 3) log4j 配置 —— 这个文件在 BMCLAPI 上是 404，必须走官方 piston-data
        if (version.Logging?.Client?.File is { } logFile)
        {
            Section("log4j 配置");
            await downloader.EnsureAsync(logFile.Url, paths.LogConfigFile(logFile.Id), logFile.Sha1);
            Info($"{logFile.Id}  {Mb(logFile.Size)}");
        }

        // 4) assetIndex
        if (version.AssetIndex is { } index)
        {
            Section("assetIndex");
            await downloader.EnsureAsync(index.Url, paths.AssetIndexFile(index.Id), index.Sha1);
            Info($"{index.Id}.json  总 {Mb(index.TotalSize)}");

            if (withAssets)
            {
                Section("assets 对象");
                var assetInstaller = new AssetInstaller(downloader);

                var lastReport = 0;
                var assetProgress = new Progress<AssetProgress>(p =>
                {
                    // 3598 个文件逐条打印太吵：按 200 个一档汇报，并同时显示新增字节数
                    if (p.Completed - lastReport < 200 && p.Completed != p.Total) return;
                    lastReport = p.Completed;
                    Dim($"{p.Completed}/{p.Total}  新增 {p.BytesDownloaded / 1024d / 1024d:N1} MB");
                });

                var assetResult = await assetInstaller.InstallAsync(
                    paths, index.Id, concurrency: 16, progress: assetProgress);

                Info($"对象       {assetResult.Total} 个");
                Info($"新下载     {assetResult.Downloaded} 个 / {assetResult.BytesDownloaded / 1024d / 1024d:N1} MB");
                Info($"已存在跳过 {assetResult.Skipped} 个");

                if (assetResult.Failures.Count > 0)
                {
                    Error($"失败 {assetResult.Failures.Count} 个:");
                    foreach (var f in assetResult.Failures.Take(10)) Dim(f);
                }
                else
                {
                    Ok("资源对象全部就绪");
                }
            }
            else
            {
                Warn("已跳过资源对象下载（加 --with-assets 才会下），游戏材质会缺失");
            }
        }

        // 5) natives 目录（1.20.1 不需要解压，但 -Djava.library.path 必须指向存在的目录）
        Directory.CreateDirectory(paths.NativesDir(versionId));

        Success("安装完成");
        return 0;
    }

    // -------------------------------------------------------------- runtime

    private static async Task<int> RuntimeAsync(string[] args)
    {
        var component = Required(args);
        var paths = new LauncherPaths(ResolveRoot(args));
        var platform = Opt(args, "--platform") ?? "windows-x64";
        var concurrency = OptInt(args, "--concurrency", 12);

        using var http = HttpFileDownloader.CreateClient();
        var downloader = new HttpFileDownloader(http);
        var installer = new MojangRuntimeInstaller(downloader, downloader.GetStringAsync);

        Section($"运行时 {component} ({platform})");
        var lastReport = 0;
        var progress = new Progress<RuntimeProgress>(p =>
        {
            if (p.Completed - lastReport < 40 && p.Completed != p.Total) return;
            lastReport = p.Completed;
            Dim($"{p.Completed}/{p.Total}  {p.BytesDownloaded / 1024d / 1024d:N1} MB");
        });

        var result = await installer.InstallAsync(platform, component, paths, concurrency, progress);

        Info($"文件       {result.TotalFiles} 个");
        Info($"下载       {result.BytesDownloaded / 1024d / 1024d:N1} MB");
        Info($"java.exe   {result.JavaExePath}");

        if (result.Failures.Count > 0)
        {
            Error($"失败 {result.Failures.Count} 个:");
            foreach (var f in result.Failures.Take(20)) Dim(f);
            return 1;
        }

        if (!result.JavaExeExists)
        {
            Error("安装完成但 bin\\java.exe 不存在");
            return 1;
        }

        // 实际跑一次，确认这个 stub 能加载同目录的 jvm.dll
        var version = RunJavaVersion(result.JavaExePath);
        Section("java -version");
        Dim(version.Trim());

        Success("运行时可用");
        return 0;
    }

    // --------------------------------------------------------------- launch

    private static async Task<int> LaunchAsync(string[] args)
    {
        var launcher = new LauncherPaths(ResolveRoot(args));

        var instanceId = Opt(args, "--instance");
        LauncherInstance? instance = null;
        InstanceLayout? layout = null;
        var paths = launcher;
        string versionId;

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            var catalog = new InstanceCatalog(await new JsonInstanceStore(launcher).LoadAsync());
            instance = catalog.Find(instanceId!)
                       ?? throw new InvalidOperationException($"找不到实例 '{instanceId}'。先用 npmc instance list 看看。");

            layout = new InstanceLayout(launcher, instance);
            layout.EnsureDirectories();
            paths = layout.Paths;
            versionId = instance.VersionId;

            Section("实例");
            Info($"名称     {instance.Name}  ({instance.Id})");
            Info($"版本     {versionId}");
            Info($"隔离     {DescribeIsolation(instance.Isolation)}");
            Info($"游戏目录 {layout.GameDirectory}");
        }
        else
        {
            versionId = Required(args);
        }

        using var http = HttpFileDownloader.CreateClient();
        var downloader = new HttpFileDownloader(http);
        var source = new VersionManifestSource(downloader.GetStringAsync);

        var version = await LoadVersionAsync(source, downloader, paths, versionId);
        var os = OsInfo.Windows64();
        var logConfig = version.Logging?.Client?.File is { } f ? paths.LogConfigFile(f.Id) : null;
        var settings = instance?.Settings ?? new InstanceSettings();

        var context = LaunchContext.OfflineProbe() with
        {
            Width = settings.WindowWidth,
            Height = settings.WindowHeight,
        };

        var plan = LaunchPlanFactory.Create(paths, versionId, version, context, os,
            memoryMb: OptInt(args, "--memory", settings.MemoryMb),
            hasCustomResolution: true,
            logConfigFile: logConfig,
            gameDirectory: layout?.GameDirectory);

        // Java 优先级：实例指定路径 → 实例指定组件 → 版本元数据声明的组件
        var javaPath = plan.JavaPath;
        if (!string.IsNullOrWhiteSpace(settings.JavaPath)) javaPath = settings.JavaPath!;
        else if (!string.IsNullOrWhiteSpace(settings.JavaComponent)) javaPath = paths.JavaExe(settings.JavaComponent!);

        if (!string.Equals(javaPath, plan.JavaPath, StringComparison.OrdinalIgnoreCase))
            plan = plan with { JavaPath = javaPath };

        if (settings.ExtraJvmArguments.Count > 0)
            plan = plan with { JvmArguments = plan.JvmArguments.Concat(settings.ExtraJvmArguments).ToList() };

        if (!File.Exists(plan.JavaPath))
        {
            Error($"找不到 Java: {plan.JavaPath}");
            Warn($"先执行: npmc runtime {version.JavaVersion?.Component ?? "java-runtime-gamma"}");
            return 1;
        }
        if (!File.Exists(paths.ClientJar(versionId)))
        {
            Error($"找不到 client jar: {paths.ClientJar(versionId)}");
            Warn($"先执行: npmc install {versionId}");
            return 1;
        }

        Directory.CreateDirectory(paths.NativesDir(versionId));

        Section("启动");
        Info($"java  {plan.JavaPath}");
        Info($"内存  {plan.JvmArguments.FirstOrDefault(a => a.StartsWith("-Xmx", StringComparison.Ordinal)) ?? "-"}");
        Info($"窗口  {settings.WindowWidth}x{settings.WindowHeight} / {plan.GameArguments.Count} 个游戏参数");
        if (layout is not null) Info($"日志  {layout.LatestLog}");

        var psi = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.ToProcessArguments()) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 java 进程");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0) Success("游戏正常退出（退出码 0）");
        else Error($"游戏退出码 {process.ExitCode}");

        return process.ExitCode;
    }

    // ------------------------------------------------------------- instance

    private static async Task<int> InstanceAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Error("instance 需要子命令：list | create | paths | remove");
            PrintHelp();
            return 1;
        }

        var action = args[0];
        var rest = args[1..];
        var launcher = new LauncherPaths(ResolveRoot(rest));
        var store = new JsonInstanceStore(launcher);
        var catalog = new InstanceCatalog(await store.LoadAsync());

        switch (action)
        {
            case "list":
            {
                Section("实例列表");

                if (catalog.Count == 0)
                {
                    Warn("还没有任何实例。");
                    Dim("npmc instance create 原版生存 --version 1.20.1");
                    return 0;
                }

                foreach (var item in catalog.Instances)
                {
                    var marker = item.Isolation switch
                    {
                        IsolationLevel.None => "[共享]",
                        IsolationLevel.Version => "[版本]",
                        _ => "[完全]",
                    };
                    Info($"{marker} {item.Id,-20} {item.Name,-20} {item.VersionId,-10} {item.Settings.MemoryMb} MB");
                }

                var problems = catalog.Validate();
                if (problems.Count > 0)
                {
                    Section("数据问题");
                    foreach (var problem in problems) Error(problem);
                    return 2;
                }

                Info($"\n共 {catalog.Count} 个实例");
                Dim(store.FilePath);
                return 0;
            }

            case "create":
            {
                var name = Required(rest);
                var versionId = Opt(rest, "--version")
                                ?? throw new ArgumentException("必须用 --version 指定游戏版本，例如 --version 1.20.1");
                var isolation = ParseIsolation(Opt(rest, "--isolation"));

                var instance = catalog.Add(name, versionId, isolation);
                var instanceLayout = new InstanceLayout(launcher, instance);
                instanceLayout.EnsureDirectories();
                await store.SaveAsync(catalog.Instances);

                Section("已创建实例");
                Info($"Id        {instance.Id}");
                Info($"名称      {instance.Name}");
                Info($"版本      {instance.VersionId}");
                Info($"隔离      {DescribeIsolation(instance.Isolation)}");
                Info($"游戏目录  {instanceLayout.GameDirectory}");
                Info($"共享根    {instanceLayout.SharedRoot}");

                Section("已建好的目录");
                foreach (var directory in instanceLayout.StableDirectories) Dim(directory);

                Info($"\n实例列表：{store.FilePath}");
                return 0;
            }

            case "paths":
            {
                var id = Required(rest);
                var instance = catalog.Find(id) ?? throw new InvalidOperationException($"找不到实例 '{id}'");
                var instanceLayout = new InstanceLayout(launcher, instance);

                Section($"实例 {instance.Name} ({instance.Id})");
                Info($"版本        {instance.VersionId}");
                Info($"隔离        {DescribeIsolation(instance.Isolation)}");
                Info($"游戏目录    {instanceLayout.GameDirectory}");
                Info($"共享根      {instanceLayout.SharedRoot}");

                Section("实例目录");
                Info($"mods          {instanceLayout.Mods}");
                Info($"resourcepacks {instanceLayout.ResourcePacks}");
                Info($"shaderpacks   {instanceLayout.ShaderPacks}");
                Info($"saves         {instanceLayout.Saves}");
                Info($"config        {instanceLayout.Config}");
                Info($"logs          {instanceLayout.Logs}");

                Section("共享目录");
                Info($"versions      {instanceLayout.Paths.Versions}");
                Info($"libraries     {instanceLayout.Paths.Libraries}");
                Info($"assets        {instanceLayout.Paths.Assets}");

                Section("现有内容");
                Info($"mods          {instanceLayout.ListFiles(instanceLayout.Mods).Count} 个文件");
                Info($"resourcepacks {instanceLayout.ListFiles(instanceLayout.ResourcePacks).Count} 个文件");
                Info($"shaderpacks   {instanceLayout.ListFiles(instanceLayout.ShaderPacks).Count} 个文件");
                return 0;
            }

            case "remove":
            {
                var id = Required(rest);
                var instance = catalog.Find(id) ?? throw new InvalidOperationException($"找不到实例 '{id}'");
                var instanceLayout = new InstanceLayout(launcher, instance);
                var deleteFiles = Has(rest, "--delete-files");

                catalog.Remove(instance.Id);
                await store.SaveAsync(catalog.Instances);
                Ok($"已从列表移除：{instance.Name} ({instance.Id})");

                if (!deleteFiles)
                {
                    Info("磁盘文件保留（加 --delete-files 才会删）");
                    return 0;
                }

                if (!instanceLayout.OwnsGameDirectory)
                {
                    Warn("该实例是无隔离模式，游戏目录就是启动器根目录 —— 拒绝删除。");
                    return 0;
                }

                var target = Path.GetFullPath(instanceLayout.GameDirectory);
                var instancesRoot = Path.GetFullPath(launcher.Instances) + Path.DirectorySeparatorChar;

                // 安全闸：只允许删除 instances 目录下的子目录
                if (!target.StartsWith(instancesRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Error($"拒绝删除：{target} 不在 instances 目录下");
                    return 1;
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                    Ok($"已删除目录 {target}");
                }
                else
                {
                    Info("目录本来就不存在");
                }
                return 0;
            }

            default:
                Error($"未知的 instance 子命令：{action}");
                PrintHelp();
                return 1;
        }
    }

    private static IsolationLevel ParseIsolation(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => IsolationLevel.Version,
        "none" => IsolationLevel.None,
        "version" => IsolationLevel.Version,
        "full" => IsolationLevel.Full,
        _ => throw new ArgumentException($"未知的隔离级别 '{value}'，可选：none | version | full"),
    };

    private static string DescribeIsolation(IsolationLevel level) => level switch
    {
        IsolationLevel.None => "无隔离",
        IsolationLevel.Version => "版本隔离",
        IsolationLevel.Full => "完全隔离",
        _ => level.ToString(),
    };

    // -------------------------------------------------------------- helpers

    private static async Task<Core.Models.VersionJson> LoadVersionAsync(
        VersionManifestSource source, HttpFileDownloader downloader, LauncherPaths paths, string versionId)
    {
        var manifest = await source.GetManifestAsync();
        var entry = manifest.Find(versionId)
                    ?? throw new InvalidOperationException($"版本清单里没有 {versionId}");

        var jsonPath = paths.VersionJson(versionId);
        await downloader.EnsureAsync(entry.Url, jsonPath, entry.Sha1);
        return Core.Models.VersionJsonSerializer.Parse(await File.ReadAllTextAsync(jsonPath));
    }

    private static int CountClasspath(LaunchPlan plan)
    {
        var raw = plan.JvmArguments.FirstOrDefault(a => a.Contains(';') && a.Contains(".jar"));
        return raw is null ? 0 : raw.Split(';').Length;
    }

    private static string RunJavaVersion(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo(javaExe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p is null) return "(无法启动)";
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return text;
        }
        catch (Exception ex)
        {
            return $"(执行失败: {ex.Message})";
        }
    }

    private static string ResolveRoot(string[] args)
    {
        var root = Opt(args, "--root");
        return string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : Path.GetFullPath(root);
    }

    private static string Required(string[] args) =>
        args.FirstOrDefault(a => !a.StartsWith("--"))
        ?? throw new ArgumentException("缺少参数（版本号或组件名）");

    private static string? Opt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int OptInt(string[] args, string name, int fallback) =>
        int.TryParse(Opt(args, name), out var v) ? v : fallback;

    private static bool Has(string[] args, string name) => args.Contains(name);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static string Mb(long bytes) => $"{bytes / 1024d / 1024d:N1} MB";

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"=== {title} ===");
        Console.ResetColor();
    }

    private static void Info(string message) => Console.WriteLine($"  {message}");
    private static void Dim(string message) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine($"    {message}"); Console.ResetColor(); }
    private static void Ok(string message) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"  ✓ {message}"); Console.ResetColor(); }
    private static void Success(string message) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"\n{message}"); Console.ResetColor(); }
    private static void Warn(string message) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"  ! {message}"); Console.ResetColor(); }
    private static void Error(string message) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"  ✗ {message}"); Console.ResetColor(); }

    private static int Unknown(string command)
    {
        Error($"未知命令: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            npmc —— NPMC Launcher 命令行探针

            用法:
              npmc probe   <版本>    [--root <目录>] [--memory <MB>]
                                     只组装参数并打印，不下载大文件、不启动游戏
              npmc install <版本>    [--root <目录>] [--with-assets]
                                     下载 client jar / libraries / log4j 配置 / assetIndex
              npmc runtime <组件名>  [--root <目录>] [--platform windows-x64] [--concurrency 12]
                                     安装 Mojang 官方 Java 运行时到 <root>\runtime\<组件名>\
              npmc launch  <版本>    [--root <目录>] [--memory <MB>] [--instance <Id>]
                                     组装参数并启动游戏；给 --instance 时用实例的目录与设置

            实例管理:
              npmc instance list                      列出所有实例
              npmc instance create <名字> --version <版本> [--isolation none|version|full]
                                                      创建实例（默认 version 隔离）
              npmc instance paths  <Id>               显示该实例的全部目录
              npmc instance remove <Id> [--delete-files]
                                                      从列表移除（默认保留磁盘文件）

            示例:
              npmc probe   1.20.1 --root .\data
              npmc install 1.20.1 --root .\data
              npmc runtime java-runtime-gamma --root .\data
              npmc instance create 原版生存 --version 1.20.1 --root .\data
              npmc launch  1.20.1 --instance 原版生存 --root .\data

            说明:
              --root 省略时使用程序所在目录（便携模式：整个文件夹可整体迁移）。
              运行时的组件名直接来自版本 JSON 的 javaVersion.component。
              隔离级别决定哪些目录共享：无隔离全共享；版本隔离共享 versions/libraries/assets；
              完全隔离连库与资源也放进实例目录，可单独拷走。
            """);
    }
}
