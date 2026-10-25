using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.Launch;

namespace NPMC.Launcher.App;

/// <summary>
/// 无头诊断：把 <see cref="MainWindowViewModel"/> 加载出来的内容写成文本文件后退出。
///
/// 为什么需要它：
///   界面没法在无图形的方式下核对（截图只能靠人看）。这个开关走的是**和界面完全相同**的
///   ViewModel 加载路径，因此能验证「实例 JSON → 列表项投影」以及「内容目录 → 内容条目」
///   这两段是否真的对，而不需要启动窗口。
///
/// 用法：
///   NPMC.Launcher.App.exe --dump-instances out.txt --root D:\path\to\launcher
/// </summary>
internal static class InstanceDump
{
    private const string Option = "--dump-instances";

    /// <summary>诊断：解析一个本地整合包文件（--exercise-modpack &lt;路径&gt;）。</summary>
    private static string? ModpackPath(string[] args)
    {
        var index = Array.FindIndex(args, arg =>
            string.Equals(arg, "--exercise-modpack", StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>是否请求了 dump 模式（用于在入口处先初始化渲染平台）。</summary>
    public static bool IsDumpRequested(string[] args) =>
        Array.FindIndex(args, arg => string.Equals(arg, Option, StringComparison.OrdinalIgnoreCase)) >= 0;

    public static bool TryRun(string[] args)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, Option, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return false;

        var outputPath = Path.GetFullPath(args[index + 1]);
        var root = LauncherRoot.Resolve(args);

        var viewModel = new MainWindowViewModel(new LauncherPaths(root));
        viewModel.LoadAsync().GetAwaiter().GetResult();

        var lines = new List<string>
        {
            $"root   = {viewModel.RootPath}",
            $"status = {viewModel.Status}",
            $"count  = {viewModel.Instances.Count}",
            $"empty  = {viewModel.IsEmpty}",
            "",
        };

        foreach (var item in viewModel.Instances)
        {
            lines.Add($"[{item.Id}]");
            lines.Add($"    name      = {item.Name}");
            lines.Add($"    version   = {item.VersionId}");
            lines.Add($"    isolation = {item.IsolationText}");
            lines.Add($"    memory    = {item.MemoryText}");
            lines.Add($"    window    = {item.WindowText}");
            lines.Add($"    summary   = {item.SummaryText}");
            lines.Add($"    content   = {item.ContentText}");
            lines.Add($"    gameDir   = {item.GameDirectory}");
        }

        // 验证启动准备链路（只组装计划、不启动进程）
        var first = viewModel.Instances.FirstOrDefault();

        // 页签可用性由实例能力决定，这里逐个列出来
        if (first is not null)
        {
            viewModel.SelectedInstance = viewModel.Instances[0];
            viewModel.RefreshCapabilitiesAsync(first).GetAwaiter().GetResult();

            lines.Add("");
            lines.Add($"[实例能力] 加载器={viewModel.DetectedLoadersText}   游戏版本={viewModel.GameVersionText}");
            foreach (var tab in viewModel.ContentTabs)
            {
                lines.Add($"    {tab.Title,-6} 可用={tab.IsEnabled}"
                          + (tab.IsEnabled ? "" : $"   原因={tab.TabTip}"));
            }
        }

        // 优先验证加载器实例：它最能暴露 inheritsFrom 没合并的问题
        var launchTarget = viewModel.Instances.FirstOrDefault(item =>
                               item.VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                           ?? first;

        if (launchTarget is not null)
        {
            var paths = new LauncherPaths(root);
            var version = GameLauncher.TryLoadInstalledVersionAsync(paths, launchTarget.VersionId)
                .GetAwaiter().GetResult();

            lines.Add("");
            if (version is null)
            {
                lines.Add($"[启动准备] 版本 {launchTarget.VersionId} 未安装（缺少 version json）");
            }
            else
            {
                var prepared = GameLauncher.Prepare(paths, launchTarget.Model, version, new LauncherSettings());

                lines.Add($"[启动准备] 实例={launchTarget.Name}  可启动={prepared.CanLaunch}");
                lines.Add($"    版本       = {version.Id}");
                lines.Add($"    根版本     = {version.RootVersionId ?? "(自身)"}");
                lines.Add($"    mainClass  = {version.MainClass}");
                lines.Add($"    库         = {version.Libraries.Count} 个");
                lines.Add($"    java       = {prepared.Plan.JavaPath}");
                lines.Add($"    workingDir = {prepared.Plan.WorkingDirectory}");
                lines.Add($"    jvmArgs    = {prepared.Plan.JvmArguments.Count}");
                lines.Add($"    gameArgs   = {prepared.Plan.GameArguments.Count}");
                lines.Add($"    classpath  = {prepared.Plan.JvmArguments.FirstOrDefault(a => a.Contains(".jar"))?.Split(';').Length} 项");

                foreach (var problem in prepared.Problems) lines.Add($"    问题: {problem}");
            }
        }

        // 验证设置持久化：改内存与分辨率 → 落盘 → 重新加载核对
        if (args.Any(arg => string.Equals(arg, "--exercise-settings", StringComparison.OrdinalIgnoreCase))
            && viewModel.SelectedInstance is not null)
        {
            viewModel.MemoryMb = 6144;
            viewModel.WindowWidth = 1600;
            viewModel.WindowHeight = 900;
            viewModel.SaveSettingsNowAsync().GetAwaiter().GetResult();

            var reloaded = new MainWindowViewModel(new LauncherPaths(root));
            reloaded.LoadAsync().GetAwaiter().GetResult();
            var again = reloaded.SelectedInstance;

            lines.Add("");
            lines.Add($"[设置持久化] memory={again?.MemoryMb} window={again?.WindowWidth}x{again?.WindowHeight}");
        }

        // 逐个页签走一遍刷新，验证内容扫描这条链路
        foreach (var kind in new[] { ContentKind.Mod, ContentKind.ResourcePack, ContentKind.ShaderPack })
        {
            viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == kind);
            viewModel.RefreshContentAsync().GetAwaiter().GetResult();

            lines.Add("");
            lines.Add($"[{kind.DisplayName()}] {viewModel.Content.Count} 个条目  目录={CurrentDirectory(viewModel, kind)}");

            foreach (var entry in viewModel.Content)
            {
                lines.Add($"    {entry.StatusText}  {entry.Name}");
                lines.Add($"        {entry.MetaText}");
            }
        }

        // 验证升级检查链路（会联网访问 Modrinth）
        if (args.Any(arg => string.Equals(arg, "--exercise-updates", StringComparison.OrdinalIgnoreCase)))
        {
            // 原版实例的模组页签是禁用的，诊断必须挑加载器实例才走得通
            var updateTarget = viewModel.Instances.FirstOrDefault(item =>
                                   item.VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                               ?? viewModel.Instances.FirstOrDefault();

            if (updateTarget is not null)
            {
                viewModel.SelectedInstance = updateTarget;
                viewModel.RefreshCapabilitiesAsync(updateTarget).GetAwaiter().GetResult();
            }

            viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
            viewModel.CheckUpdatesAsync().GetAwaiter().GetResult();

            // 图标由页签刷新时后台补，给它时间落盘
            Thread.Sleep(4000);

            lines.Add("");
            lines.Add($"[升级检查] 可升级={viewModel.UpdateCount}");
            lines.Add($"    status = {viewModel.Status}");

            foreach (var item in viewModel.Content)
            {
                var badge = item.IsFromModrinth ? "[M]"
                    : item.IsFromCurseForge ? "[C]"
                    : item.IsUnidentified ? "[?]"
                    : "[-]";

                var channel = item.HasChannel ? $"[{item.ChannelBadge}]" : "[ ]";

                lines.Add($"    {badge}{channel} {(item.HasUpdate ? "可升级" : "  -  ")}  {item.Name,-30} {item.UpdateText}");
            }
        }

        // 验证资源包/光影的自动识别（图标地址缓存同样适用）
        if (args.Any(arg => string.Equals(arg, "--exercise-content-icons", StringComparison.OrdinalIgnoreCase)))
        {
            var target = viewModel.Instances.FirstOrDefault(item =>
                             !item.VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                         ?? viewModel.Instances.FirstOrDefault();

            if (target is not null)
            {
                viewModel.SelectedInstance = target;
                viewModel.RefreshCapabilitiesAsync(target).GetAwaiter().GetResult();

                lines.Add("");
                lines.Add($"[内容识别] 实例 {target.Name}");

                foreach (var kind in new[] { ContentKind.ResourcePack, ContentKind.ShaderPack })
                {
                    viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == kind);

                    // 后台识别是 fire-and-forget，等它把地址写进缓存
                    Thread.Sleep(4000);

                    lines.Add($"    {kind.DisplayName()}：{viewModel.Content.Count} 个文件");

                    foreach (var item in viewModel.Content)
                    {
                        var badge = item.IsFromModrinth ? "[M]"
                            : item.IsFromCurseForge ? "[C]"
                            : item.IsUnidentified ? "[?]"
                            : "[-]";

                        lines.Add($"      {badge} {item.Name}");
                    }
                }
            }
        }

        // 验证"搜索 → 依赖计划"链路（联网，但不下载）
        if (args.Any(arg => string.Equals(arg, "--exercise-search", StringComparison.OrdinalIgnoreCase)))
        {
            // 优先选加载器实例：那才是最容易踩"用 profile 名当游戏版本"坑的场景
            var target = viewModel.Instances.FirstOrDefault(item =>
                             item.VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                         ?? viewModel.Instances.FirstOrDefault();

            if (target is not null)
            {
                viewModel.SelectedInstance = target;
                viewModel.RefreshCapabilitiesAsync(target).GetAwaiter().GetResult();
            }

            viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
            viewModel.SearchForDiagnosticsAsync("sodium").GetAwaiter().GetResult();
            lines.Add("");
            lines.Add($"[搜索] 结果 {viewModel.SearchResults.Count} 个 —— {viewModel.SearchSummaryText}");
            lines.Add($"    搜索按钮可用 = {viewModel.SearchCommand.CanExecute(null)}");
            foreach (var hit in viewModel.SearchResults)
                lines.Add($"    {hit.Title,-30} {hit.MetaText}");

            if (viewModel.SearchResults.FirstOrDefault() is { } topHit)
            {
                // 选中第一条 → 触发详情加载（异步 fire-and-forget，这里轮询等它完成）
                viewModel.SelectedSearchResult = topHit;

                for (var i = 0; i < 150 && viewModel.DetailBody.StartsWith("正在加载", StringComparison.Ordinal); i++)
                    Thread.Sleep(100);

                lines.Add($"[详情] {viewModel.DetailTitle}");
                lines.Add($"    {viewModel.DetailSubtitle}");
                lines.Add($"    url  = {viewModel.DetailUrl}");
                lines.Add($"    body = {viewModel.DetailBody}");

                // 图标是异步补的，等第一个到齐
                for (var i = 0; i < 100 && viewModel.SearchResults.Count > 0
                     && viewModel.SearchResults[0].Icon is null; i++)
                    Thread.Sleep(100);

                lines.Add($"[图标] 列表 {viewModel.SearchResults.Count(item => item.Icon is not null)}"
                          + $"/{viewModel.SearchResults.Count} 个已加载"
                          + "（无头诊断没有渲染平台，解码不可用；真实窗口才会有图标）");

                // 空查询 → 浏览热门
                viewModel.SearchForDiagnosticsAsync("").GetAwaiter().GetResult();
                lines.Add($"[热门] {viewModel.SearchSummaryText}");
                foreach (var hot in viewModel.SearchResults.Take(4))
                    lines.Add($"    {hot.Title,-28} {hot.MetaText}");

                viewModel.PrepareInstallForDiagnosticsAsync(topHit).GetAwaiter().GetResult();

                lines.Add($"[计划] 可见={viewModel.IsPlanVisible} 共 {viewModel.PlanLines.Count} 行");
                foreach (var line in viewModel.PlanLines)
                    lines.Add($"    {line.Label,-14} {line.Detail}");

                lines.Add($"    可选版本 {viewModel.InstallVersions.Count} 个，当前 {viewModel.SelectedInstallVersion?.Label}");
                foreach (var choice in viewModel.InstallVersions.Take(4))
                    lines.Add($"      [{choice.TypeBadge}] {choice.Label}    ({choice.TypeLabel})");

                // 切到第二个版本，确认计划会跟着重算
                if (viewModel.InstallVersions.Count > 1)
                {
                    viewModel.SelectInstallVersionForDiagnosticsAsync(1).GetAwaiter().GetResult();

                    lines.Add($"[切换版本] 当前 {viewModel.SelectedInstallVersion?.Label}");
                    foreach (var line in viewModel.PlanLines)
                        lines.Add($"    {line.Label,-14} {line.Detail}");
                }

                lines.Add($"    status = {viewModel.Status}");

                // 切到别的内容页签：搜索词、结果、计划都应被清空
                viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.ResourcePack);
                lines.Add($"[切换页签→资源包] 搜索词=\"{viewModel.SearchQuery}\" 结果={viewModel.SearchResults.Count} "
                          + $"计划可见={viewModel.IsPlanVisible} 可选版本={viewModel.InstallVersions.Count}");
            }
        }

        // 验证下载页：切过去 → 等版本清单拉回来 → 打印前几个
        if (args.Any(arg => string.Equals(arg, "--exercise-downloads", StringComparison.OrdinalIgnoreCase)))
        {
            viewModel.ShowDownloadsCommand.Execute(null);

            // Navigate 里的加载是 fire-and-forget，这里轮询等它完成（诊断代码，不用 await 改结构）
            for (var i = 0; i < 300 && viewModel.DownloadVersions.Count == 0; i++)
                Thread.Sleep(100);

            lines.Add("");
            lines.Add($"[下载页] 版本 {viewModel.DownloadVersions.Count} 个 —— {viewModel.DownloadStatus}");
            lines.Add($"    isDownloadsPage = {viewModel.IsDownloadsPage}");

            foreach (var version in viewModel.DownloadVersions.Take(5))
                lines.Add($"    [{version.TypeBadge}] {version.Id,-14} {version.ReleasedText}   {version.TypeLabel}");

            lines.Add($"    选中 = {viewModel.SelectedDownloadVersion?.Id ?? "(无)"}"
                      + $"   副标题 = {viewModel.DownloadVersionSubtitle}");

            // 按钮是否可点，取决于命令的 CanExecute —— 这里直接问它，而不是看界面
            lines.Add($"    下载按钮可用 = {viewModel.DownloadCommand.CanExecute(null)}");

            // 名称跟随、加载器选择、重名拦截
            if (viewModel.DownloadVersions.Count >= 3)
            {
                lines.Add("");
                lines.Add("[下载页·名称与加载器]");

                viewModel.SelectedDownloadVersion = viewModel.DownloadVersions[1];
                lines.Add($"    切到 {viewModel.SelectedDownloadVersion?.Id} → 名称 = {viewModel.DownloadInstanceName}");

                viewModel.InstallFabric = false;
                lines.Add($"    关闭 Fabric → 名称 = {viewModel.DownloadInstanceName}");

                viewModel.InstallFabric = true;
                for (var i = 0; i < 150 && viewModel.FabricLoaderVersions.Count == 0; i++) Thread.Sleep(100);

                lines.Add($"    Fabric 加载器 {viewModel.FabricLoaderVersions.Count} 个，选中 {viewModel.SelectedFabricLoader?.Version}");
                lines.Add($"    名称 = {viewModel.DownloadInstanceName}");

                // 手动改名之后不再跟随版本
                viewModel.DownloadInstanceName = "自定义名字";
                viewModel.SelectedDownloadVersion = viewModel.DownloadVersions[2];
                lines.Add($"    手改后切版本 → 名称 = {viewModel.DownloadInstanceName}");

                // 重名：用已有实例的名字
                if (viewModel.Instances.Count > 0)
                {
                    viewModel.DownloadInstanceName = viewModel.Instances[0].Name;
                    lines.Add($"    与已有实例同名 = {viewModel.DownloadConflictsWithExisting}，"
                              + $"按钮可用 = {viewModel.DownloadCommand.CanExecute(null)}");
                }
            }
        }

        // 验证整合包解析（本地文件；不安装）
        if (ModpackPath(args) is { } modpackPath)
        {
            viewModel.LoadModpackFileAsync(modpackPath).GetAwaiter().GetResult();

            lines.Add("");
            lines.Add("[整合包]");
            lines.Add($"    标题   = {viewModel.ModpackTitle}");
            lines.Add($"    摘要   = {viewModel.ModpackSummary}");
            lines.Add($"    状态   = {viewModel.ModpackStatus}");
            lines.Add($"    可安装 = {viewModel.InstallModpackCommand.CanExecute(null)}");
        }

        File.WriteAllLines(outputPath, lines);
        return true;
    }

    private static string CurrentDirectory(MainWindowViewModel viewModel, ContentKind kind)
        => viewModel.SelectedInstance?.DirectoryFor(kind) ?? "(无选中实例)";
}
