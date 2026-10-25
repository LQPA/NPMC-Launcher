using System.Diagnostics;
using System.Text;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Mirrors;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Launch;
using NPMC.Launcher.Infrastructure.Minecraft;
using NPMC.Launcher.Infrastructure.Mirrors;
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
                    ? $"  [{first} → 经 TUN/代理]"
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

        Section("网络路径");
        foreach (var host in new[] { "launchermeta.mojang.com", "piston-meta.mojang.com", "bmclapi2.bangbang93.com" })
        {
            var url = $"https://{host}/";
            Info($"{host,-32}{TrimPath(DescribeNetworkPath(url))}");
        }

        var mirrorEntry = "https://bmclapi2.bangbang93.com/";
        if (DescribeNetworkPath(mirrorEntry).Contains("TUN", StringComparison.Ordinal))
            warnings.Add("BMCLAPI 流量经过本机代理（fake-IP）：测速会失真且可能更慢。建议把 bangbang93.com / sakura.sld.tw / openbmclapi.* 在代理规则里设为 DIRECT。");

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

    /// <summary>去掉 DescribeNetworkPath 前面的两个空格，便于对齐输出。</summary>
    private static string TrimPath(string description) => description.TrimStart();

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

            镜像源:
              npmc mirror test [--source official|bmclapi] [--version 1.20.1] [--sample 1024] [--repeat 3]
                                                      对每个资源类型，官方与 BMCLAPI 并排探测，
                                                      打印跳转链、状态、耗时（--repeat 取多次最快值）

            环境诊断:
              npmc doctor [--root <目录>]
                                                      检查进程权限、Java 运行时、代理/TUN、
                                                      下载源连通性、目录与实例状态，并列出自检建议

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
