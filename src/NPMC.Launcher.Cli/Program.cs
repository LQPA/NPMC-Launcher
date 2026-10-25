using System.Diagnostics;
using System.Text;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Mods;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Diagnostics;
using NPMC.Launcher.Infrastructure.Hashing;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Launch;
using NPMC.Launcher.Infrastructure.Minecraft;
using NPMC.Launcher.Infrastructure.Mirrors;
using NPMC.Launcher.Infrastructure.Modrinth;
using NPMC.Launcher.Infrastructure.Network;
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
                "mirror"   => await MirrorAsync(args[1..]),
                "doctor"   => await DoctorAsync(args[1..]),
                "settings" => await SettingsAsync(args[1..]),
                "net"      => await NetAsync(args[1..]),
                "mods"     => await ModsAsync(args[1..]),
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
        var launcherSettings = await LoadSettingsAsync(paths);
        var router = CreateRouter(args, launcherSettings);
        var source = CreateSource(router, downloader);

        var version = await LoadVersionAsync(source, downloader, paths, versionId, router);

        var context = LaunchContext.OfflineProbe();
        var os = OsInfo.Windows64();
        var plan = LaunchPlanFactory.Create(paths, versionId, version, context, os,
            memoryMb: OptInt(args, "--memory", launcherSettings.DefaultMemoryMb),
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
        var launcherSettings = await LoadSettingsAsync(paths);
        var router = CreateRouter(args, launcherSettings);
        var source = CreateSource(router, downloader);

        var version = await LoadVersionAsync(source, downloader, paths, versionId, router);
        var os = OsInfo.Windows64();

        // 统计每个文件最终从哪个源拿到，便于判断镜像是否真的帮上忙
        var mirrorHits = 0;
        var officialHits = 0;
        var fallbacks = new List<string>();

        void CountSource(string label, DownloadOutcome outcome)
        {
            if (!outcome.Downloaded) return;

            if (IsMirrorUrl(router, outcome.Url)) mirrorHits++;
            else officialHits++;

            // 有失败尝试说明镜像这一路不通、退回了下一个候选
            if (outcome.Attempts.Count > 0)
                fallbacks.Add($"{label}：{outcome.Attempts[0].Error} → 改用 {ShortHost(outcome.Url)}");
        }

        var effective = version.Libraries
            .Where(lib => RuleEvaluator.IsAllowed(lib.Rules, os, FeatureSet.ForLaunch(true)))
            .ToList();

        // 1) client jar
        if (version.Downloads?.Client is { } client)
        {
            Section("client.jar");
            var clientOutcome = await downloader.EnsureFromAsync(
                Candidates(router, client.Url, ResourceKind.ClientJar),
                paths.ClientJar(versionId), client.Sha1);
            CountSource($"client.jar", clientOutcome);
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
            var libraryOutcome = await downloader.EnsureFromAsync(
                Candidates(router, artifact.Url, ResourceKind.Library),
                paths.LibraryFile(artifact.Path), artifact.Sha1);
            CountSource(lib.Name, libraryOutcome);
            done++;
            if (done % 20 == 0) Dim($"{done}/{effective.Count}");
        }
        Ok($"完成 {done} 个库");

        // 3) log4j 配置 —— 这个文件在 BMCLAPI 上是 404，必须走官方 piston-data
        if (version.Logging?.Client?.File is { } logFile)
        {
            Section("log4j 配置");
            await downloader.EnsureFromAsync(
                Candidates(router, logFile.Url, ResourceKind.LoggingConfig),
                paths.LogConfigFile(logFile.Id), logFile.Sha1);
            Info($"{logFile.Id}  {Mb(logFile.Size)}  （该类别不可镜像，恒走官方）");
        }

        // 4) assetIndex
        if (version.AssetIndex is { } index)
        {
            Section("assetIndex");
            var indexOutcome = await downloader.EnsureFromAsync(
                Candidates(router, index.Url, ResourceKind.AssetIndex),
                paths.AssetIndexFile(index.Id), index.Sha1);
            CountSource($"assetIndex {index.Id}", indexOutcome);
            Info($"{index.Id}.json  总 {Mb(index.TotalSize)}");

            if (withAssets)
            {
                Section("assets 对象");
                var assetInstaller = new AssetInstaller(downloader, router);

                var lastReport = 0;
                var assetProgress = new Progress<AssetProgress>(p =>
                {
                    // 3598 个文件逐条打印太吵：按 200 个一档汇报，并同时显示新增字节数
                    if (p.Completed - lastReport < 200 && p.Completed != p.Total) return;
                    lastReport = p.Completed;
                    Dim($"{p.Completed}/{p.Total}  新增 {p.BytesDownloaded / 1024d / 1024d:N1} MB");
                });

                var assetResult = await assetInstaller.InstallAsync(
                    paths, index.Id, concurrency: launcherSettings.DownloadConcurrency, progress: assetProgress);

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

        Section("下载源");
        Info($"镜像 {mirrorHits} 个 / 官方 {officialHits} 个   （首选：{DescribeSource(router.Settings.Source)}）");

        if (fallbacks.Count > 0)
        {
            Section($"回退记录（{fallbacks.Count} 个）");
            foreach (var fallback in fallbacks.Take(15)) Dim(fallback);
            if (fallbacks.Count > 15) Dim($"…还有 {fallbacks.Count - 15} 条");
        }

        if (router.Settings.Source == DownloadSource.Bmclapi && mirrorHits == 0)
            Warn("首选镜像但没有文件走镜像 —— 检查镜像连通性，或用 npmc mirror test 对比");

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
        var launcherSettings = await LoadSettingsAsync(paths);
        var router = CreateRouter(args, launcherSettings);
        var source = CreateSource(router, downloader);

        var version = await LoadVersionAsync(source, downloader, paths, versionId, router);
        var os = OsInfo.Windows64();
        var logConfig = version.Logging?.Client?.File is { } f ? paths.LogConfigFile(f.Id) : null;
        var settings = instance?.Settings ?? new InstanceSettings();

        var context = LaunchContext.OfflineProbe() with
        {
            Width = settings.WindowWidth,
            Height = settings.WindowHeight,
        };

        var plan = LaunchPlanFactory.Create(paths, versionId, version, context, os,
            memoryMb: OptInt(args, "--memory", instance?.Settings.MemoryMb ?? launcherSettings.DefaultMemoryMb),
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

    // -------------------------------------------------------------- mirror

    private static async Task<int> MirrorAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "test")
        {
            Error("用法: npmc mirror test [--source official|bmclapi] [--version 1.20.1] [--sample 1024]");
            return 1;
        }

        var rest = args[1..];
        var versionId = Opt(rest, "--version") ?? "1.20.1";
        var sample = OptInt(rest, "--sample", 1024);
        var settings = new MirrorSettings
        {
            Source = string.Equals(Opt(rest, "--source"), "bmclapi", StringComparison.OrdinalIgnoreCase)
                ? DownloadSource.Bmclapi
                : DownloadSource.Official,
        };

        var router = new MirrorRouter(settings);
        using var http = HttpFileDownloader.CreateClient();
        var downloader = new HttpFileDownloader(http);
        var probe = new MirrorProbe();   // 自带 AllowAutoRedirect = false
        var source = new VersionManifestSource(downloader.GetStringAsync);

        Section($"镜像源测试（首选源：{DescribeSource(settings.Source)}）");

        // 先把真实元数据拿到手，才有真实的 URL / sha1 可测
        var manifest = await source.GetManifestAsync();
        var entry = manifest.Find(versionId) ?? throw new InvalidOperationException($"清单里没有 {versionId}");
        var version = await source.GetVersionJsonAsync(entry);

        var resources = new List<(ResourceKind Kind, string OfficialUrl, string Label)>
        {
            (ResourceKind.VersionManifest, VersionManifestSource.OfficialManifestUrl, "版本清单"),
            (ResourceKind.VersionMetadata, entry.Url, $"{versionId} 版本 JSON"),
        };

        if (version.Downloads?.Client is { } client)
            resources.Add((ResourceKind.ClientJar, client.Url, "client.jar"));

        var library = version.Libraries.FirstOrDefault(lib => !string.IsNullOrEmpty(lib.Downloads?.Artifact?.Url));
        if (library is not null)
            resources.Add((ResourceKind.Library, library.Downloads!.Artifact!.Url, $"库 {library.Name}"));

        if (version.AssetIndex is { } assetIndex)
        {
            resources.Add((ResourceKind.AssetIndex, assetIndex.Url, $"assetIndex {assetIndex.Id}"));

            try
            {
                var indexJson = await downloader.GetStringAsync(assetIndex.Url);
                using var document = System.Text.Json.JsonDocument.Parse(indexJson);
                var firstObject = document.RootElement.GetProperty("objects").EnumerateObject().First();
                var hash = firstObject.Value.GetProperty("hash").GetString();
                if (!string.IsNullOrEmpty(hash) && hash.Length >= 2)
                    resources.Add((ResourceKind.AssetObject,
                        $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                        $"资源对象 {hash[..8]}…"));
            }
            catch (Exception ex)
            {
                Warn($"取资源对象样本失败：{ex.Message}");
            }
        }

        if (version.Logging?.Client?.File is { } logFile)
            resources.Add((ResourceKind.LoggingConfig, logFile.Url, "log4j 配置（必须官方）"));

        var repeat = Math.Max(1, OptInt(rest, "--repeat", 1));
        var rows = new List<(string Label, string Source, IReadOnlyList<ProbeResult> Results)>();

        foreach (var resource in resources)
        {
            Section(resource.Label);
            Dim($"{resource.Kind}  {resource.OfficialUrl}");

            foreach (var target in router.Resolve(resource.OfficialUrl, resource.Kind))
            {
                var results = new List<ProbeResult>(repeat);
                for (var i = 0; i < repeat; i++)
                    results.Add(await probe.ProbeAsync(target.Url, sample));

                rows.Add((resource.Label, DescribeSource(target.Source), results));

                var first = results[0];
                var okTimes = results.Where(r => r.Success).Select(r => r.Elapsed.TotalMilliseconds).ToList();
                var status = first.Success ? $"{first.FinalStatus}" : $"{first.FinalStatus} {first.Error}";
                var timing = okTimes.Count == 0
                    ? "            "
                    : repeat > 1
                        ? $"min {okTimes.Min(),4:N0}ms avg {okTimes.Average(),4:N0}ms"
                        : $"{first.Elapsed.TotalMilliseconds,6:N0}ms     ";

                Console.ForegroundColor = first.Success ? ConsoleColor.Gray : ConsoleColor.Red;
                Console.WriteLine(
                    $"  {DescribeSource(target.Source),-8} {status,-6} 跳转{first.RedirectCount}  {timing}  {first.BytesRead,5}B  {ShortHost(first.FinalUrl)}{DescribeNetworkPath(first.FinalUrl)}");
                Console.ResetColor();

                foreach (var hop in first.Hops)
                    Dim($"    {hop.StatusCode}  {Truncate(hop.Url, 108)}");
            }
        }

        Section("按源汇总");
        foreach (var group in rows.GroupBy(row => row.Source))
        {
            var totalResources = group.Count();
            var succeeded = group.Count(row => row.Results.Any(r => r.Success));

            // 每个资源取多次采样里的最快值，再对资源求平均 —— 比单次采样稳定得多
            var perResourceBest = group
                .Where(row => row.Results.Any(r => r.Success))
                .Select(row => row.Results.Where(r => r.Success).Min(r => r.Elapsed.TotalMilliseconds))
                .ToList();

            var average = perResourceBest.Count > 0 ? perResourceBest.Average() : 0;
            Info($"{group.Key,-8} 成功 {succeeded}/{totalResources}   每资源最快耗时平均 {average:N0}ms");
        }

        return 0;
    }

    private static string DescribeSource(DownloadSource source) =>
        source == DownloadSource.Bmclapi ? "BMCLAPI" : "官方";

    private static string ShortHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var path = uri.AbsolutePath;
        if (path.Length > 60) path = path[..60] + "…";
        return uri.Host + path;
    }

    /// <summary>
    /// 判断一段流量是否被本机的 TUN/代理接管。
    ///
    /// Clash/Surge 一类工具的 fake-IP 模式会把域名解析到 198.18.0.0/15（RFC 2544 保留段），
    /// 流量由代理接管。看到这个地址就说明"耗时"反映的是代理路径，而不是直连速度。
    /// </summary>
    private static string DescribeNetworkPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";

        if (NetworkPathCache.TryGetValue(uri.Host, out var cached)) return cached;

        string description;
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(uri.Host);
            if (addresses.Length == 0)
            {
                description = "  [解析失败]";
            }
            else
            {
                var first = addresses[0];
                var bytes = first.GetAddressBytes();
                var isFakeIp = bytes.Length == 4 && bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);

                description = isFakeIp
                    ? $"  [{first} fake-IP]"
                    : $"  [{first}]";
            }
        }
        catch
        {
            description = "  [解析失败]";
        }

        NetworkPathCache[uri.Host] = description;
        return description;
    }

    private static readonly Dictionary<string, string> NetworkPathCache = new(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------- doctor

    private static async Task<int> DoctorAsync(string[] args)
    {
        var launcher = new LauncherPaths(ResolveRoot(args));
        var router = new MirrorRouter();
        using var http = HttpFileDownloader.CreateClient();
        var probe = new MirrorProbe();

        var warnings = new List<string>();

        Section("环境");
        Info($"操作系统    {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Info($"体系结构    {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}（OS: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}）");
        Info($"运行时      .NET {Environment.Version}");
        Info($"进程完整性  {ProcessContext.Describe()}");
        Info($"启动器根    {launcher.Root}");

        var tempDirectory = Path.GetTempPath();
        var tempWritable = TryWriteProbe(tempDirectory);
        Info($"TEMP        {tempDirectory}");
        Info($"TEMP 可写   {YesNo(tempWritable)}");
        if (!tempWritable)
            warnings.Add("TEMP 不可写：受限令牌下测试与临时文件会失败，请改用工作区内的目录。");

        var processHandleBlocked = !CanOpenOwnProcess();
        Info($"进程句柄    {(processHandleBlocked ? "被禁止" : "可用")}");
        if (processHandleBlocked)
            warnings.Add("无法 OpenProcess 自身：监听游戏进程退出不能用 EnableRaisingEvents，应改用 WaitForExitAsync 或轮询。");

        var inSandbox = tempDirectory.Contains("dsh-", StringComparison.OrdinalIgnoreCase);
        Info($"受限环境     {(inSandbox ? "是（TEMP 含 dsh- 前缀）" : "否")}");

        if (ProcessContext.IsLowIntegrity)
            warnings.Add("进程处于 Low 完整性：系统会拒绝 shell COM、ShellExecute 与 explorer.exe 启动，"
                       + "也就是说无法从本进程打开资源管理器。请从开始菜单启动，而不是从受限的终端启动。");

        Section("Java");
        var installations = JavaLocator.Discover(launcher);
        if (installations.Count == 0)
        {
            Warn("没有找到任何 Java");
            warnings.Add("没有 Java：1.20.1 需要 17+。可用 npmc runtime java-runtime-gamma 安装内置运行时。");
        }
        else
        {
            foreach (var java in installations)
            {
                var tag = java.IsBundled ? "[内置]" : "[系统]";
                var version = java.MajorVersion > 0 ? $"Java {java.MajorVersion}" : "版本未知";
                Info($"{tag} {version,-10} {java.Path}");
            }

            if (!installations.Any(java => java.MajorVersion >= 17))
                warnings.Add("没有 17+ 的 Java：1.20.1 及以上需要 Java 17。");
        }

        Section("网络路径（fake-IP 只说明 TUN 接管了 DNS，不代表走代理）");
        foreach (var host in new[] { "launchermeta.mojang.com", "piston-meta.mojang.com", "bmclapi2.bangbang93.com" })
        {
            var systemAddresses = NetworkDiagnostics.ResolveSystem(host);
            var publicAddresses = await NetworkDiagnostics.ResolvePublicAsync(host, http);
            var fakeIp = systemAddresses.Any(NetworkDiagnostics.IsFakeIp);

            Info(host);
            Dim($"  系统解析  {(systemAddresses.Count > 0 ? string.Join(", ", systemAddresses) : "(失败)")}{(fakeIp ? "   ← fake-IP" : "")}");
            Dim($"  公共解析  {(publicAddresses.Count > 0 ? string.Join(", ", publicAddresses) : "(查询失败)")}");
        }

        var mirrorAddresses = NetworkDiagnostics.ResolveSystem("bmclapi2.bangbang93.com");
        if (mirrorAddresses.Any(NetworkDiagnostics.IsFakeIp))
            warnings.Add("本机 TUN 接管了 DNS（fake-IP）：域名被解析成 198.18.x 假地址。这不代表流量走代理 —— 走向由代理规则决定，DIRECT 规则下 DNS 依然返回假地址。要判断规则是否生效，请看下面下载源的实测耗时，或到代理软件的「连接」页查该连接命中的规则。");

        Section("下载源连通性（各取前 512 字节）");
        foreach (var target in router.Resolve(VersionManifestSource.OfficialManifestUrl, ResourceKind.VersionManifest))
        {
            var result = await probe.ProbeAsync(target.Url, 512);
            var status = result.Success ? result.FinalStatus.ToString() : "失败";
            Console.ForegroundColor = result.Success ? ConsoleColor.Gray : ConsoleColor.Red;
            Console.WriteLine(
                $"  {DescribeSource(target.Source),-8} {status,-6} {result.Elapsed.TotalMilliseconds,6:N0}ms  跳转{result.RedirectCount}  {ShortHost(result.FinalUrl)}");
            Console.ResetColor();

            if (!result.Success) warnings.Add($"{DescribeSource(target.Source)} 不可用：{result.Error}");
        }

        Section("目录");
        foreach (var (name, path) in new (string Name, string Path)[]
                 {
                     ("assets", launcher.Assets),
                     ("libraries", launcher.Libraries),
                     ("versions", launcher.Versions),
                     ("runtime", launcher.Runtime),
                     ("instances", launcher.Instances),
                 })
        {
            if (Directory.Exists(path))
                Info($"{name,-10} {DirectorySize(path) / 1024d / 1024d,8:N1} MB   {path}");
            else
                Dim($"{name,-10}        —   {path}");
        }

        Section("实例");
        try
        {
            var catalog = new InstanceCatalog(await new JsonInstanceStore(launcher).LoadAsync());
            if (catalog.Count == 0)
            {
                Dim("（无）");
            }
            else
            {
                foreach (var instance in catalog.Instances)
                    Info($"{DescribeIsolation(instance.Isolation),-6} {instance.Id,-18} {instance.VersionId,-10} {instance.Settings.MemoryMb} MB");

                foreach (var problem in catalog.Validate()) warnings.Add($"实例数据问题：{problem}");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"读取实例列表失败：{ex.Message}");
        }

        Section("结论");
        if (warnings.Count == 0)
        {
            Ok("未发现明显问题");
        }
        else
        {
            foreach (var warning in warnings) Warn(warning);
        }

        return 0;
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    private static bool TryWriteProbe(string directory)
    {
        try
        {
            var name = "npmc-probe-" + Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(path);
            Directory.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanOpenOwnProcess()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(Environment.ProcessId);
            process.EnableRaisingEvents = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    // --------------------------------------------------------------- net

    /// <summary>
    /// 实测官方源与镜像源当前谁更快，并把结论写进 settings.json。
    ///
    /// 为什么不写死"国内用镜像"：实测同一台机器在 TUN 开关、不同代理规则下，
    /// 两个源的相对快慢会翻转。让程序自己测一次，比在代码里猜地区可靠。
    /// </summary>
    private static async Task<int> NetAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "selftest")
        {
            Error("用法: npmc net selftest [--version 1.20.1] [--samples 3] [--dry-run]");
            return 1;
        }

        var rest = args[1..];
        var versionId = Opt(rest, "--version") ?? "1.20.1";
        var samples = Math.Clamp(OptInt(rest, "--samples", 3), 1, 10);
        var dryRun = Has(rest, "--dry-run");

        var paths = new LauncherPaths(ResolveRoot(rest));
        var settings = await LoadSettingsAsync(paths);

        using var http = HttpFileDownloader.CreateClient();
        var downloader = new HttpFileDownloader(http);
        var probe = new MirrorProbe();

        Section("下载源自测");

        // 拿真实 URL 来测：清单（小文件、元数据）+ 一个库（批量下载的代表）
        var resources = new List<(ResourceKind Kind, string Url, string Label)>
        {
            (ResourceKind.VersionManifest, VersionManifestSource.OfficialManifestUrl, "版本清单"),
        };

        try
        {
            var source = new VersionManifestSource(downloader.GetStringAsync);
            var manifest = await source.GetManifestAsync();
            if (manifest.Find(versionId) is { } entry)
            {
                var version = await source.GetVersionJsonAsync(entry);
                var library = version.Libraries.FirstOrDefault(lib => !string.IsNullOrEmpty(lib.Downloads?.Artifact?.Url));
                if (library is not null)
                    resources.Add((ResourceKind.Library, library.Downloads!.Artifact!.Url, "库（样本）"));
            }
            else
            {
                Warn($"清单里没有 {versionId}，只测版本清单");
            }
        }
        catch (Exception ex)
        {
            Warn($"取样本失败（{ex.Message}），只测版本清单");
        }

        var totals = new Dictionary<DownloadSource, double>();
        var failures = new Dictionary<DownloadSource, int>();

        foreach (var source in new[] { DownloadSource.Official, DownloadSource.Bmclapi })
        {
            // 自测要拿到"纯单源"数据，所以关掉回退
            var router = new MirrorRouter(settings.ToMirrorSettings() with { Source = source, AllowFallback = false });
            var total = 0d;
            var failed = 0;

            foreach (var resource in resources)
            {
                var target = router.Resolve(resource.Url, resource.Kind).FirstOrDefault();
                if (target is null)
                {
                    failed++;
                    continue;
                }

                var best = double.MaxValue;
                for (var i = 0; i < samples; i++)
                {
                    var result = await probe.ProbeAsync(target.Url, 512);
                    if (result.Success) best = Math.Min(best, result.Elapsed.TotalMilliseconds);
                }

                if (best == double.MaxValue)
                {
                    failed++;
                    Info($"{DescribeSource(source),-8} {resource.Label,-12} 失败");
                }
                else
                {
                    total += best;
                    Info($"{DescribeSource(source),-8} {resource.Label,-12} {best,6:N0}ms");
                }
            }

            totals[source] = total;
            failures[source] = failed;
        }

        var officialTotal = totals[DownloadSource.Official];
        var mirrorTotal = totals[DownloadSource.Bmclapi];

        Section("结论");
        Info($"官方合计 {officialTotal,6:N0}ms（失败 {failures[DownloadSource.Official]}）");
        Info($"镜像合计 {mirrorTotal,6:N0}ms（失败 {failures[DownloadSource.Bmclapi]}）");

        DownloadSource winner;
        if (officialTotal <= 0 && mirrorTotal <= 0)
        {
            Error("两个源都不可用，请检查网络与代理设置");
            return 1;
        }

        if (officialTotal <= 0) winner = DownloadSource.Bmclapi;
        else if (mirrorTotal <= 0) winner = DownloadSource.Official;
        else winner = mirrorTotal < officialTotal ? DownloadSource.Bmclapi : DownloadSource.Official;

        Ok($"建议首选源：{DescribeSource(winner)}");

        if (dryRun)
        {
            Dim("--dry-run：未写入设置");
            return 0;
        }

        var store = new JsonSettingsStore(paths);
        await store.SaveAsync(settings with { PreferredSource = winner });
        Ok($"已写入 {store.FilePath}");
        Dim("可用 npmc settings set source official|bmclapi 手动改回");

        return 0;
    }

    // --------------------------------------------------------------- mods

    /// <summary>
    /// 检查 mods 目录里的模组是否有新版本。
    ///
    /// 流程：本地算 SHA1 → 批量反查 Modrinth 认领"这是哪个项目的哪个版本"
    /// → 按实例的游戏版本与加载器取版本列表 → 判断有没有更新。
    /// 加载器过滤是必须的：否则会给 Forge 整合包推 Fabric 版本的模组。
    /// </summary>
    private static async Task<int> ModsAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "check")
        {
            Error("用法: npmc mods check [--instance <Id> | --version <版本>] [--root <目录>] [--prerelease]");
            return 1;
        }

        var rest = args[1..];
        var withPrerelease = Has(rest, "--prerelease");
        var launcher = new LauncherPaths(ResolveRoot(rest));

        LauncherInstance? instance = null;
        InstanceLayout? layout = null;
        string versionId;

        var instanceId = Opt(rest, "--instance");
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            var catalog = new InstanceCatalog(await new JsonInstanceStore(launcher).LoadAsync());
            instance = catalog.Find(instanceId!)
                       ?? throw new InvalidOperationException($"找不到实例 '{instanceId}'。用 npmc instance list 查看。");

            layout = new InstanceLayout(launcher, instance);
            versionId = instance.VersionId;
        }
        else
        {
            versionId = Opt(rest, "--version") ?? Required(rest);
        }

        var paths = layout?.Paths ?? launcher;
        var gameDirectory = layout?.GameDirectory ?? launcher.Root;
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        Section("模组升级检查");
        Info($"实例     {(instance is null ? "-" : $"{instance.Name} ({instance.Id})")}");
        Info($"版本     {versionId}");
        Info($"mods     {modsDirectory}");

        if (!Directory.Exists(modsDirectory))
        {
            Warn("mods 目录不存在，没有可检查的模组");
            return 0;
        }

        // 加载器从版本 JSON 的 libraries 推断
        var version = await GameLauncher.TryLoadInstalledVersionAsync(paths, versionId);
        IReadOnlyList<string> loaders = version is null ? Array.Empty<string>() : LoaderDetector.Detect(version);

        if (version is null) Warn("本地没有安装该版本，无法判断加载器（将不按加载器过滤）");
        Info($"加载器   {(loaders.Count > 0 ? string.Join(", ", loaders) : "原版 / 未知")}");

        var mods = ContentScanner.Scan(modsDirectory, ContentKind.Mod).Where(mod => !mod.IsDirectory).ToList();
        if (mods.Count == 0)
        {
            Warn("mods 目录里没有 .jar");
            return 0;
        }

        Info($"文件     {mods.Count} 个");

        // 1) 本地哈希
        Dim("计算 SHA1…");
        var hashByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            try
            {
                hashByPath[mod.Path] = await FileHashing.Sha1Async(mod.Path);
            }
            catch (Exception ex)
            {
                Warn($"跳过 {mod.FileName}：{ex.GetBaseException().Message}");
            }
        }

        if (hashByPath.Count == 0)
        {
            Error("没有可读取的模组文件");
            return 1;
        }

        // 2) 批量反查
        using var http = HttpFileDownloader.CreateClient();
        var client = new ModrinthClient(http);

        Dim("查询 Modrinth（按哈希）…");
        var matched = await client.GetVersionsByHashesAsync(hashByPath.Values.ToList());

        // 3) 每个命中的项目单独取版本列表（按游戏版本 + 加载器过滤）
        var projectVersions = new Dictionary<string, IReadOnlyList<ModrinthVersion>>();
        var projectIds = matched.Values.Select(v => v.ProjectId).Distinct().ToList();
        Dim($"命中 {matched.Count} 个，需要查询 {projectIds.Count} 个项目…");

        foreach (var projectId in projectIds)
        {
            try
            {
                projectVersions[projectId] = await client.GetProjectVersionsAsync(projectId, versionId, loaders);
            }
            catch (Exception ex)
            {
                Warn($"项目 {projectId} 查询失败：{ex.GetBaseException().Message}");
            }
        }

        // 4) 组装本地信息并判定
        var localMods = hashByPath
            .Select(pair => new LocalModInfo(
                pair.Key,
                Path.GetFileName(pair.Key),
                pair.Value,
                matched.TryGetValue(pair.Value, out var matchedVersion) ? matchedVersion : null))
            .ToList();

        var updates = UpdatePlanner.Plan(localMods, projectVersions, withPrerelease);

        Section("结果");
        var unknown = 0;
        var upToDate = 0;

        foreach (var mod in localMods.OrderBy(m => m.FileName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (mod.MatchedVersion is null)
            {
                unknown++;
                Dim($"[未知]   {mod.FileName}   Modrinth 上查不到（自制或未上传）");
                continue;
            }

            var update = updates.FirstOrDefault(u => u.LocalPath == mod.Path);
            if (update is null)
            {
                upToDate++;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"  [最新]   {mod.FileName}   {mod.MatchedVersion.VersionNumber}");
                Console.ResetColor();
                continue;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [可升级] {mod.FileName}   {update.Current.VersionNumber} → {update.Latest.VersionNumber}"
                              + $"   ({update.Latest.VersionType})");
            Console.ResetColor();
        }

        Section("汇总");
        Ok($"可升级 {updates.Count} 个 / 已最新 {upToDate} 个 / 未知 {unknown} 个");

        if (updates.Count > 0)
        {
            Dim("升级功能将在下一步接入界面（含下载新版、旧版移入 .trash）");
        }

        return 0;
    }

    // ------------------------------------------------------------ settings

    private static async Task<int> SettingsAsync(string[] args)
    {
        var paths = new LauncherPaths(ResolveRoot(args));
        var store = new JsonSettingsStore(paths);

        if (args.Length == 0 || args[0] == "show")
        {
            var current = await LoadSettingsAsync(paths);

            Section("全局设置");
            Info($"首选下载源   {DescribeSource(current.PreferredSource)}");
            Info($"镜像基地址   {current.BmclapiBaseUrl}");
            Info($"允许回退     {YesNo(current.AllowSourceFallback)}");
            Info($"默认内存     {current.DefaultMemoryMb} MB");
            Info($"并发下载     {current.DownloadConcurrency}");
            Info($"语言         {current.Language}");
            Dim(store.FilePath);
            return 0;
        }

        if (args[0] != "set" || args.Length < 3)
        {
            Error("用法: npmc settings show  |  npmc settings set <键> <值>");
            Dim("键: source(official|bmclapi) / memory / concurrency / mirror / fallback(true|false)");
            return 1;
        }

        var key = args[1].ToLowerInvariant();
        var value = args[2];
        var settings = await LoadSettingsAsync(paths);

        switch (key)
        {
            case "source":
                settings = settings with
                {
                    PreferredSource = string.Equals(value, "bmclapi", StringComparison.OrdinalIgnoreCase)
                        ? DownloadSource.Bmclapi
                        : DownloadSource.Official,
                };
                break;

            case "memory":
                if (!int.TryParse(value, out var memory) || memory < 512)
                {
                    Error("内存必须是 >= 512 的整数（MB）");
                    return 1;
                }
                settings = settings with { DefaultMemoryMb = memory };
                break;

            case "concurrency":
                if (!int.TryParse(value, out var concurrency) || concurrency is < 1 or > 64)
                {
                    Error("并发数必须在 1~64 之间");
                    return 1;
                }
                settings = settings with { DownloadConcurrency = concurrency };
                break;

            case "mirror":
                settings = settings with { BmclapiBaseUrl = value.TrimEnd('/') };
                break;

            case "fallback":
                settings = settings with
                {
                    AllowSourceFallback = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                };
                break;

            default:
                Error($"未知的键：{key}");
                return 1;
        }

        await store.SaveAsync(settings);
        Ok($"已写入 {store.FilePath}");
        return 0;
    }

    // -------------------------------------------------------------- helpers

    /// <summary>由 --source 参数或全局设置构造镜像路由器（默认官方优先，镜像作为回退）。</summary>
    private static MirrorRouter CreateRouter(string[] args, LauncherSettings settings)
    {
        var sourceOption = Opt(args, "--source");
        var source = sourceOption is null
            ? settings.PreferredSource
            : string.Equals(sourceOption, "bmclapi", StringComparison.OrdinalIgnoreCase)
                ? DownloadSource.Bmclapi
                : DownloadSource.Official;

        return new MirrorRouter(settings.ToMirrorSettings() with { Source = source });
    }

    /// <summary>读全局设置；配置文件损坏时退回默认值，而不是让整个命令失败。</summary>
    private static async Task<LauncherSettings> LoadSettingsAsync(LauncherPaths paths)
    {
        try
        {
            return await new JsonSettingsStore(paths).LoadAsync();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    /// <summary>按资源类别自动选源的文本抓取器：逐个候选尝试，全部失败才抛。</summary>
    private static VersionManifestSource CreateSource(MirrorRouter router, HttpFileDownloader downloader)
    {
        Func<string, ResourceKind, CancellationToken, Task<string>> fetch = async (url, kind, ct) =>
        {
            Exception? last = null;

            foreach (var target in router.Resolve(url, kind))
            {
                try
                {
                    return await downloader.GetStringAsync(target.Url, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                }
            }

            throw last ?? new InvalidOperationException($"无法获取 {url}");
        };

        return new VersionManifestSource(fetch);
    }

    /// <summary>把官方 URL 展开成候选地址列表。</summary>
    private static IReadOnlyList<string> Candidates(MirrorRouter router, string url, ResourceKind kind)
        => router.Resolve(url, kind).Select(target => target.Url).ToList();

    /// <summary>判断某个成功地址是不是镜像入口。</summary>
    private static bool IsMirrorUrl(MirrorRouter router, string url)
        => url.StartsWith(router.Settings.BmclapiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static async Task<Core.Models.VersionJson> LoadVersionAsync(
        VersionManifestSource source, HttpFileDownloader downloader, LauncherPaths paths,
        string versionId, MirrorRouter router)
    {
        var manifest = await source.GetManifestAsync();
        var entry = manifest.Find(versionId)
                    ?? throw new InvalidOperationException($"版本清单里没有 {versionId}");

        var jsonPath = paths.VersionJson(versionId);
        await downloader.EnsureFromAsync(
            Candidates(router, entry.Url, ResourceKind.VersionMetadata), jsonPath, entry.Sha1);

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
              npmc install <版本>    [--root <目录>] [--with-assets] [--source official|bmclapi]
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

            镜像源:
              npmc mirror test [--source official|bmclapi] [--version 1.20.1] [--sample 1024] [--repeat 3]
                                                      对每个资源类型，官方与 BMCLAPI 并排探测，
                                                      打印跳转链、状态、耗时（--repeat 取多次最快值）

            环境诊断:
              npmc doctor [--root <目录>]
                                                      检查进程权限、Java 运行时、代理/TUN、
                                                      下载源连通性、目录与实例状态，并列出自检建议

            设置:
              npmc settings show
                                                      显示全局设置（settings.json）
              npmc settings set <键> <值>
                                                      键：source(official|bmclapi) / memory / concurrency /
                                                      mirror / fallback(true|false)

            网络:
              npmc net selftest [--version 1.20.1] [--samples 3] [--dry-run]
                                                      实测官方与镜像当前谁更快，结论写入 settings.json

            模组:
              npmc mods check [--instance <Id> | --version <版本>] [--root <目录>] [--prerelease]
                                                      按 SHA1 反查 Modrinth，按游戏版本与加载器
                                                      判断哪些模组有新版本

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
