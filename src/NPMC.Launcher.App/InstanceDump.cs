using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Launch;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;
using NPMC.Launcher.Infrastructure.CurseForge;
using NPMC.Launcher.Infrastructure.Launch;
using NPMC.Launcher.Infrastructure.Storage;

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

    /// <summary>
    /// 读日志尾部。游戏正在写 latest.log，必须以共享模式打开，否则会抛"文件被占用"。
    /// </summary>
    private static string ReadTailSafe(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "(没有日志)";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var all = new List<string>();
            while (reader.ReadLine() is { } line) all.Add(line.Trim());

            return string.Join(" / ", all.TakeLast(lines));
        }
        catch (Exception ex)
        {
            return $"(读取失败：{ex.GetBaseException().Message})";
        }
    }

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
                foreach (var notice in prepared.Notices) lines.Add($"    提示: {notice}");

                // 打印完整命令行：出问题时要能自己复现一次
                if (args.Any(arg => string.Equals(arg, "--print-launch-command", StringComparison.OrdinalIgnoreCase)))
                {
                    lines.Add("");
                    lines.Add("[命令行]");
                    lines.Add($"EXE|{prepared.Plan.JavaPath}");
                    lines.Add($"DIR|{prepared.Plan.WorkingDirectory}");
                    foreach (var argument in prepared.Plan.JvmArguments) lines.Add($"JVM|{argument}");
                    foreach (var argument in prepared.Plan.GameArguments) lines.Add($"GAME|{argument}");
                }
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

        // 验证全局设置页：字段 ↔ settings.json ↔ 启动计划
        if (args.Any(arg => string.Equals(arg, "--exercise-global-settings", StringComparison.OrdinalIgnoreCase)))
        {
            var settingsStore = new JsonSettingsStore(new LauncherPaths(root));
            var original = settingsStore.Load();

            lines.Add("");
            lines.Add("[全局设置]");
            lines.Add($"    文件 {viewModel.SettingsPath}");

            viewModel.ShowSettingsCommand.Execute(null);
            Thread.Sleep(2000);

            for (var i = 0; i < 60 && viewModel.SettingsJavaChoices.Count <= 1; i++) Thread.Sleep(200);
            // 运行时清单要联网，给它最多 20 秒
            for (var i = 0; i < 80 && viewModel.RuntimeInstallStatus.StartsWith("正在"); i++) Thread.Sleep(250);

            lines.Add($"    页面={viewModel.IsSettingsPage} 实例面板={viewModel.ShowOverview}（应为 False）");
            lines.Add($"    源={viewModel.SettingsSourceIndex} 镜像={viewModel.SettingsMirrorUrl} "
                      + $"并发={viewModel.SettingsConcurrency} 超时={viewModel.SettingsTimeoutSeconds} "
                      + $"代理={Describe(viewModel.SettingsProxy)} 默认内存={viewModel.SettingsDefaultMemory}");
            lines.Add($"    Java 候选 {viewModel.SettingsJavaChoices.Count} 个，选中 {viewModel.SettingsJavaChoice?.Label}");
            lines.Add($"    运行时组件 {viewModel.RuntimeChoices.Count} 个：{viewModel.RuntimeInstallStatus}");

            // 改几项 → 落盘 → 直接读文件核对
            viewModel.SettingsSourceIndex = 1;
            viewModel.SettingsMirrorUrl = "https://bmclapi2.bangbang93.com/";
            viewModel.SettingsConcurrency = 7;
            viewModel.SettingsTimeoutSeconds = 240;
            viewModel.SettingsProxy = "http://127.0.0.1:7890";
            viewModel.SettingsDefaultMemory = 8192;
            viewModel.SaveSettingsPageNowAsync().GetAwaiter().GetResult();

            var reloaded = settingsStore.Load();
            lines.Add($"    落盘：源={reloaded.PreferredSource} 镜像={reloaded.BmclapiBaseUrl} "
                      + $"并发={reloaded.DownloadConcurrency} 超时={reloaded.HttpTimeoutSeconds} "
                      + $"代理={Describe(reloaded.HttpProxy)} 默认内存={reloaded.DefaultMemoryMb}");

            // 全局默认 Java 要真的影响启动计划（实例没有单独指定时才生效）
            var javaChoice = viewModel.SettingsJavaChoices.FirstOrDefault(choice => choice.Path is not null);
            if (javaChoice?.Path is { Length: > 0 } javaPath)
            {
                viewModel.SettingsJavaChoice = javaChoice;
                viewModel.SaveSettingsPageNowAsync().GetAwaiter().GetResult();

                var target = viewModel.Instances.FirstOrDefault(item => item.JavaPath is null);
                if (target is not null)
                {
                    var version = GameLauncher.TryLoadInstalledVersionAsync(
                        new LauncherPaths(root), target.VersionId).GetAwaiter().GetResult();

                    if (version is not null)
                    {
                        var prepared = GameLauncher.Prepare(
                            new LauncherPaths(root), target.Model, version, settingsStore.Load());

                        lines.Add($"    默认 Java 生效：{target.Name} → {prepared.Plan.JavaPath}");
                        lines.Add($"    与所选一致={string.Equals(prepared.Plan.JavaPath, javaPath, StringComparison.OrdinalIgnoreCase)}");
                    }
                }
            }

            // 还原用户原来的设置：诊断不该顺手改掉别人的配置
            settingsStore.SaveAsync(original).GetAwaiter().GetResult();
            lines.Add($"    已还原原设置：源={original.PreferredSource} 并发={original.DownloadConcurrency} "
                      + $"默认内存={original.DefaultMemoryMb} Java={Describe(original.DefaultJavaPath)}");
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
                if (entry is null) continue;
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

            // 默认只看正式版；打开"含测试版"再查一次，验证 beta 能被认出来
            viewModel.SettingsIncludePrereleaseUpdates = true;
            viewModel.CheckUpdatesAsync().GetAwaiter().GetResult();
            Thread.Sleep(1500);

            lines.Add($"[含测试版] 可升级={viewModel.UpdateCount}  status = {viewModel.Status}");
            foreach (var item in viewModel.Content.Where(item => item.HasUpdate).Take(4))
                lines.Add($"    可升级  {item.Name}  {item.UpdateText}");
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
            // 允许点名实例：--exercise-launch <实例名>
            var launchIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-launch", StringComparison.OrdinalIgnoreCase));
            var requestedName = launchIndex >= 0 && launchIndex + 1 < args.Length ? args[launchIndex + 1] : null;

            var target = (requestedName is null
                             ? null
                             : viewModel.Instances.FirstOrDefault(item => item.Name == requestedName))
                         ?? viewModel.Instances.FirstOrDefault(item =>
                             item.VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                         ?? viewModel.Instances.FirstOrDefault();

            if (target is not null)
            {
                viewModel.SelectedInstance = target;
                viewModel.RefreshCapabilitiesAsync(target).GetAwaiter().GetResult();
            }

            // 页签与关键词都可指定：模组 / 资源包 / 光影 共用同一条搜索安装链路
            // --exercise-kind resourcepack --exercise-query faithful
            var kindIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-kind", StringComparison.OrdinalIgnoreCase));
            var kindName = kindIndex >= 0 && kindIndex + 1 < args.Length ? args[kindIndex + 1] : null;
            var kind = kindName?.ToLowerInvariant() switch
            {
                "resourcepack" or "pack" => ContentKind.ResourcePack,
                "shader" or "shaderpack" => ContentKind.ShaderPack,
                "datapack" or "datapacks" => ContentKind.DataPack,
                _ => ContentKind.Mod,
            };

            var queryIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-query", StringComparison.OrdinalIgnoreCase));
            var query = queryIndex >= 0 && queryIndex + 1 < args.Length ? args[queryIndex + 1] : "sodium";

            viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == kind);
            lines.Add("");
            lines.Add($"[页签] {kind.DisplayName()}  下载按钮={viewModel.DownloadButtonText}  "
                      + $"筛选框={viewModel.IsContentFilterVisible} 列表={viewModel.ShowContentRows} "
                      + $"搜索面板={viewModel.IsSearchPanelVisible}");

            // 数据包按世界存放：先进入一个世界，否则下载入口不会出现
            if (kind == ContentKind.DataPack)
            {
                viewModel.SelectedWorld = viewModel.Worlds.FirstOrDefault();
                viewModel.ManageDataPacksCommand.Execute(null);

                for (var i = 0; i < 60 && !viewModel.IsManagingDataPacks; i++) Thread.Sleep(100);

                lines.Add($"    世界：{viewModel.DataPackWorld?.Name ?? "(无)"}"
                          + $"  管理页={viewModel.IsManagingDataPacks}"
                          + $"  筛选框={viewModel.IsContentFilterVisible}");

                // 输入筛选词但没有命中时，提示必须是"没匹配"而不是"文件夹是空的"
                viewModel.ContentFilterQuery = "ad";
                viewModel.RefreshContentAsync().GetAwaiter().GetResult();
                lines.Add($"    [空提示] 行数={viewModel.Content.Count} 提示={viewModel.ContentEmptyHint}");
                viewModel.ContentFilterQuery = "";
                viewModel.RefreshContentAsync().GetAwaiter().GetResult();
            }

            viewModel.OpenContentDownloadCommand.Execute(null);
            lines.Add($"[下载模式] 筛选框={viewModel.IsContentFilterVisible} 列表={viewModel.ShowContentRows} "
                      + $"搜索面板={viewModel.IsSearchPanelVisible}");

            // 分类：模组是内置列表，资源包/光影要联网取（有缓存就用缓存）
            for (var i = 0; i < 80 && viewModel.SearchCategories.Count <= 1; i++)
                Thread.Sleep(100);

            lines.Add($"    分类 {viewModel.SearchCategories.Count} 项："
                      + string.Join("、", viewModel.SearchCategories.Take(10).Select(option => option.Display))
                      + (viewModel.SearchCategories.Count > 10 ? " …" : ""));

            if (viewModel.IsSecondaryCategoryVisible)
                lines.Add($"    {viewModel.SecondaryCategoryLabel} {viewModel.SearchSecondaryCategories.Count} 项："
                          + string.Join("、", viewModel.SearchSecondaryCategories.Select(option => option.Display)));

            string? Arg(string name)
            {
                var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            int FindIndex(IReadOnlyList<SearchCategoryOption> options, string name)
            {
                for (var i = 1; i < options.Count; i++)
                {
                    if (string.Equals(options[i].Display, name, StringComparison.OrdinalIgnoreCase)) return i;
                }

                return -1;
            }

            // 主分类（风格/特性）；找不到就去次要维度（分辨率/性能）里找
            var categoryName = Arg("--exercise-category");
            if (categoryName is not null)
            {
                var primary = FindIndex(viewModel.SearchCategories, categoryName);
                if (primary > 0)
                {
                    viewModel.SearchCategoryIndex = primary;
                    lines.Add($"    已选分类：{viewModel.SelectedSearchCategoryName}（来源={viewModel.SelectedSearchSource}）");
                }
                else
                {
                    var secondary = FindIndex(viewModel.SearchSecondaryCategories, categoryName);
                    if (secondary > 0)
                    {
                        viewModel.SearchSecondaryIndex = secondary;
                        lines.Add($"    已选{viewModel.SecondaryCategoryLabel}：{categoryName}（来源={viewModel.SelectedSearchSource}）");
                    }
                    else
                    {
                        lines.Add($"    没找到分类「{categoryName}」");
                    }
                }
            }

            var secondaryName = Arg("--exercise-secondary");
            if (secondaryName is not null)
            {
                var secondaryIndex = FindIndex(viewModel.SearchSecondaryCategories, secondaryName);
                if (secondaryIndex > 0)
                {
                    viewModel.SearchSecondaryIndex = secondaryIndex;
                    lines.Add($"    已选{viewModel.SecondaryCategoryLabel}：{secondaryName}（来源={viewModel.SelectedSearchSource}）");
                }
                else
                {
                    lines.Add($"    没找到{viewModel.SecondaryCategoryLabel}「{secondaryName}」");
                }
            }

            viewModel.SearchForDiagnosticsAsync(query).GetAwaiter().GetResult();
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

                lines.Add($"    可选版本 {viewModel.InstallVersions.Count} 个，当前 {viewModel.SelectedInstallVersion?.Display}");
                foreach (var choice in viewModel.InstallVersions.Take(4))
                    lines.Add($"      [{choice.TypeBadge}] {choice.Display}    ({choice.TypeLabel})");

                // 切到第二个版本，确认计划会跟着重算
                if (viewModel.InstallVersions.Count > 1)
                {
                    viewModel.SelectInstallVersionForDiagnosticsAsync(1).GetAwaiter().GetResult();

                    lines.Add($"[切换版本] 当前 {viewModel.SelectedInstallVersion?.Display}");
                    foreach (var line in viewModel.PlanLines)
                        lines.Add($"    {line.Label,-14} {line.Detail}");
                }

                // 同一个项目换一个版本再装一次：验证"已有 A 版本时下载 B 版本"。
                // 以前这条链路会静默失败（界面什么都不说，文件也没下来）。
                if (args.Any(arg => string.Equals(arg, "--exercise-reinstall-version", StringComparison.OrdinalIgnoreCase)))
                {
                    lines.Add("");
                    lines.Add("[换版本重装]");

                    void DumpMods(string label)
                    {
                        var directory = viewModel.SelectedInstance?.ModsDirectory;
                        var names = directory is null || !Directory.Exists(directory)
                            ? Array.Empty<string>()
                            : Directory.GetFiles(directory)
                                .Select(Path.GetFileName)
                                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                .ToArray()!;

                        lines.Add($"    {label}：{string.Join(" | ", names)}");
                    }

                    DumpMods("装之前");

                    // 当前计划已被切到"另一个版本"，先装它
                    viewModel.ConfirmInstallForDiagnosticsAsync().GetAwaiter().GetResult();
                    lines.Add($"    第一次安装：{viewModel.Status}");
                    DumpMods("第一次之后");

                    // 对同一个项目再准备一次（默认落到最新正式版），装第二个版本
                    viewModel.PrepareInstallForDiagnosticsAsync(topHit).GetAwaiter().GetResult();
                    lines.Add($"    重新准备：可选版本={viewModel.InstallVersions.Count}"
                              + $" 计划行={viewModel.PlanLines.Count}"
                              + $" 首个={viewModel.PlanLines.FirstOrDefault()?.Detail}");

                    // 走**界面用的那条路**（属性 setter），而不是诊断专用绕过版本
                    if (viewModel.InstallVersions.Count > 1)
                    {
                        var before = viewModel.PlanLines.FirstOrDefault()?.Detail ?? "";

                        viewModel.SelectedInstallVersion = viewModel.InstallVersions[1];

                        for (var i = 0; i < 50; i++)
                        {
                            Thread.Sleep(100);
                            if ((viewModel.PlanLines.FirstOrDefault()?.Detail ?? "") != before) break;
                        }

                        lines.Add($"    setter 切版本后：计划={viewModel.PlanLines.FirstOrDefault()?.Detail}"
                                  + $"  状态={viewModel.Status}");
                    }

                    viewModel.ConfirmInstallForDiagnosticsAsync().GetAwaiter().GetResult();
                    lines.Add($"    第二次安装：{viewModel.Status}");
                    DumpMods("第二次之后");
                }

                // 真的下载一次（可选）：装完搜索结果必须还在。
                // 以前 ConfirmInstallAsync 会清空 SearchResults，界面就只剩搜索框和"第 N 页"。
                if (args.Any(arg => string.Equals(arg, "--exercise-search-install", StringComparison.OrdinalIgnoreCase)))
                {
                    var resultsBefore = viewModel.SearchResults.Count;
                    var summaryBefore = viewModel.SearchSummaryText;

                    // 装之前先记下已有文件，装完只看"新出现的那几行"是否已经认领
                    var before = viewModel.Content
                        .Select(item => Path.GetFileName(item.Entry.Path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    viewModel.ConfirmInstallForDiagnosticsAsync().GetAwaiter().GetResult();

                    lines.Add($"[安装后] 结果 {resultsBefore}→{viewModel.SearchResults.Count} 个"
                              + $"，摘要保留={viewModel.SearchSummaryText == summaryBefore}"
                              + $"，下载模式={viewModel.IsContentDownloadMode}"
                              + $"，计划可见={viewModel.IsPlanVisible}");

                    // 新装的行必须当场带来源徽标（M / C），否则用户看到的就是一行空白，
                    // 要等重启（后台识别一个实例一个页签只跑一次）才会亮。
                    foreach (var row in viewModel.Content.Where(row =>
                                 !before.Contains(Path.GetFileName(row.Entry.Path))))
                    {
                        lines.Add($"    [认领] {Path.GetFileName(row.Entry.Path)}"
                                  + $" 识别={row.IsIdentified} Modrinth={row.IsFromModrinth}"
                                  + $" CurseForge={row.IsFromCurseForge}");
                    }

                    // 点图标 → 下载界面：应直接把这个项目的版本下拉摆出来（不用重新搜）
                    var clickable = viewModel.Content.FirstOrDefault(row =>
                        !before.Contains(Path.GetFileName(row.Entry.Path)));

                    if (clickable is not null)
                    {
                        viewModel.InstallVersions.Clear();
                        clickable.DownloadCommand.Execute(null);

                        for (var i = 0; i < 120 && !viewModel.IsPlanVisible; i++) Thread.Sleep(100);

                        lines.Add($"    [点图标] {clickable.Name} → 下载模式={viewModel.IsContentDownloadMode}"
                                  + $" 计划可见={viewModel.IsPlanVisible}"
                                  + $" 可选版本={viewModel.InstallVersions.Count}"
                                  + $" 当前={viewModel.SelectedInstallVersion?.Display ?? "(无)"}");
                    }

                    // 空列表提示：文件夹里有文件、只是被筛选挡住时，不能报"文件夹是空的"
                    viewModel.ContentFilterQuery = "zzz-不存在的名字";
                    viewModel.RefreshContentAsync().GetAwaiter().GetResult();

                    lines.Add($"    [筛选空] 行数={viewModel.Content.Count} 提示={viewModel.ContentEmptyHint}");
                    viewModel.ContentFilterQuery = "";
                    viewModel.RefreshContentAsync().GetAwaiter().GetResult();

                    // 导出 .mrpack：能被 Modrinth 认出来的文件进 files[]，其余进 overrides
                    var mrpackIndex = Array.FindIndex(args, arg =>
                        string.Equals(arg, "--exercise-mrpack", StringComparison.OrdinalIgnoreCase));

                    if (mrpackIndex >= 0 && mrpackIndex + 1 < args.Length)
                    {
                        var mrpackTarget = args[mrpackIndex + 1];
                        viewModel.ExportMrpackAsync(mrpackTarget).GetAwaiter().GetResult();

                        lines.Add($"    [mrpack] {viewModel.ExportStatus}");

                        if (File.Exists(mrpackTarget))
                        {
                            using var archive = ZipFile.OpenRead(mrpackTarget);
                            var indexEntry = archive.GetEntry("modrinth.index.json");

                            if (indexEntry is not null)
                            {
                                using var stream = indexEntry.Open();
                                using var document = JsonDocument.Parse(stream);
                                var indexRoot = document.RootElement;

                                var dependencies = indexRoot.GetProperty("dependencies");
                                lines.Add($"      index: name={indexRoot.GetProperty("name")} "
                                          + $"files={indexRoot.GetProperty("files").GetArrayLength()} "
                                          + $"deps={string.Join(",", dependencies.EnumerateObject().Select(p => p.Name + "=" + p.Value))}");

                                foreach (var file in indexRoot.GetProperty("files").EnumerateArray().Take(3))
                                    lines.Add($"      file: {file.GetProperty("path")} "
                                              + $"sha512={(file.GetProperty("hashes").GetProperty("sha512").GetString() ?? "").Length} 字符");
                            }

                            lines.Add($"      条目：{string.Join(" | ", archive.Entries.Take(8).Select(entry => entry.FullName))}");
                        }

                        // 只写下载清单（不打包 overrides）：验证"包最小、但没有配置"
                        if (args.Any(arg => string.Equals(arg, "--exercise-mrpack-list-only", StringComparison.OrdinalIgnoreCase)))
                        {
                            viewModel.ExportBundleExtra = false;
                            var listOnly = mrpackTarget + ".list.mrpack";
                            viewModel.ExportMrpackAsync(listOnly).GetAwaiter().GetResult();

                            if (File.Exists(listOnly))
                            {
                                using var listArchive = ZipFile.OpenRead(listOnly);
                                lines.Add($"    [清单模式] 条目 {listArchive.Entries.Count} 个："
                                          + string.Join(", ", listArchive.Entries.Select(entry => entry.FullName)));
                            }

                            viewModel.ExportBundleExtra = true;
                        }
                    }
                }

                lines.Add($"    status = {viewModel.Status}");

                // 切到另一种内容页签：搜索词、结果、计划都应被清空
                var otherKind = kind == ContentKind.ResourcePack ? ContentKind.ShaderPack : ContentKind.ResourcePack;
                viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == otherKind);
                lines.Add($"[切换页签→{otherKind.DisplayName()}] 搜索词=\"{viewModel.SearchQuery}\" 结果={viewModel.SearchResults.Count} "
                          + $"计划可见={viewModel.IsPlanVisible} 可选版本={viewModel.InstallVersions.Count} "
                          + $"下载模式={viewModel.IsContentDownloadMode} 列表={viewModel.ShowContentRows}");
            }
        }

        // 验证下载页：切过去 → 等版本清单拉回来 → 打印前几个
        // 单独导出一次 .mrpack（不搜索、不安装）：验证"导出内容清单"的过滤规则
        var mrpackOnlyIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--exercise-mrpack-only", StringComparison.OrdinalIgnoreCase));

        if (mrpackOnlyIndex >= 0 && mrpackOnlyIndex + 1 < args.Length)
        {
            viewModel.SelectedInstance ??= viewModel.Instances.FirstOrDefault();

            var target = args[mrpackOnlyIndex + 1];

            if (viewModel.SelectedInstance is null)
            {
                lines.Add("[mrpack] 没有选中实例，跳过");
            }
            else
            {
                // 自定义导出目录（投影/地图等）：等价于往规则文件追加一行
                var extraIndex = Array.FindIndex(args, arg =>
                    string.Equals(arg, "--exercise-mrpack-extra", StringComparison.OrdinalIgnoreCase));

                if (extraIndex >= 0 && extraIndex + 1 < args.Length)
                {
                    viewModel.NewExportExtraPath = args[extraIndex + 1];
                    viewModel.AddExportExtraPathCommand.Execute(null);

                    lines.Add($"    [自定义目录] {viewModel.ExportStatus}");
                    lines.Add($"      {viewModel.ExportRulesSummary}");
                }

                viewModel.ExportMrpackAsync(target).GetAwaiter().GetResult();

                lines.Add("");
                lines.Add($"[mrpack] {viewModel.ExportStatus}");

                if (File.Exists(target))
                {
                    using var archive = ZipFile.OpenRead(target);
                    lines.Add($"      条目 {archive.Entries.Count} 个：{string.Join(" | ", archive.Entries.Select(entry => entry.FullName))}");
                }
            }
        }

        // 导出表单必须"跟着实例走"：换实例后包名与内容摘要都要换
        if (args.Any(arg => string.Equals(arg, "--exercise-export-form", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[导出表单跟随实例]");

            foreach (var instance in viewModel.Instances.Take(2))
            {
                viewModel.SelectedInstance = instance;
                lines.Add($"    {instance.Name}：名称={viewModel.ExportPackName}");
                lines.Add($"      摘要(打包)={viewModel.ExportSelectionHint}");

                viewModel.ExportBundleExtra = false;
                lines.Add($"      摘要(仅清单)={viewModel.ExportSelectionHint}");
                viewModel.ExportBundleExtra = true;
            }
        }

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

            // Java 运行时状态：已装与缺少两种情况都要能看到
            lines.Add("");
            lines.Add("[Java 运行时]");

            foreach (var probeId in new[] { "1.20.1", viewModel.DownloadVersions.FirstOrDefault()?.Id })
            {
                if (string.IsNullOrWhiteSpace(probeId)) continue;

                var probe = viewModel.DownloadVersions.FirstOrDefault(item => item.Id == probeId);
                if (probe is null) continue;

                viewModel.SelectedDownloadVersion = probe;
                Thread.Sleep(2500);

            }
            // 名称跟随、加载器选择、重名拦截
            if (viewModel.DownloadVersions.Count >= 3)
            {
                lines.Add("");
                lines.Add("[下载页·名称与加载器]");

                viewModel.SelectedDownloadVersion = viewModel.DownloadVersions[1];
                lines.Add($"    切到 {viewModel.SelectedDownloadVersion?.Id} → 名称 = {viewModel.DownloadInstanceName}");

                viewModel.SelectedLoader = LoaderKind.Vanilla;
                lines.Add($"    切到原版 → 名称 = {viewModel.DownloadInstanceName}");

                // 等三家加载器查完（Fabric / Forge / NeoForge 并行）
                for (var i = 0; i < 300 && viewModel.IsCheckingLoaders; i++) Thread.Sleep(100);

                lines.Add($"    可用加载器：Fabric={viewModel.CanSelectFabric} "
                          + $"Forge={viewModel.CanSelectForge} NeoForge={viewModel.CanSelectNeoForge}"
                          + $"  检查中={viewModel.IsCheckingLoaders}");
                lines.Add($"    缓存文件={viewModel.LoaderCachePath}（存在={File.Exists(viewModel.LoaderCachePath)}）");

                if (viewModel.CanSelectFabric)
                {
                    viewModel.SelectedLoader = LoaderKind.Fabric;
                    lines.Add($"    Fabric 加载器 {viewModel.FabricLoaderVersions.Count} 个，选中 {viewModel.SelectedFabricLoader?.Version}");
                    lines.Add($"    名称 = {viewModel.DownloadInstanceName}");
                }

                if (viewModel.CanSelectForge)
                {
                    viewModel.SelectedLoader = LoaderKind.Forge;
                    lines.Add($"    Forge 加载器 {viewModel.ForgeLoaderVersions.Count} 个，选中 {viewModel.SelectedForgeLoader?.Loader}");
                    lines.Add($"    名称 = {viewModel.DownloadInstanceName}");
                }

                if (viewModel.CanSelectNeoForge)
                {
                    viewModel.SelectedLoader = LoaderKind.NeoForge;
                    lines.Add($"    NeoForge 加载器 {viewModel.NeoForgeLoaderVersions.Count} 个，选中 {viewModel.SelectedNeoForgeLoader?.Loader}");
                    lines.Add($"    名称 = {viewModel.DownloadInstanceName}");
                }

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

        // 验证下载进度真的在动：采样 DownloadProgress
        if (args.Any(arg => string.Equals(arg, "--exercise-download-progress", StringComparison.OrdinalIgnoreCase)))
        {
            var progressIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-download-progress", StringComparison.OrdinalIgnoreCase));
            var wanted = progressIndex >= 0 && progressIndex + 1 < args.Length ? args[progressIndex + 1] : "1.20.1";

            viewModel.ShowDownloadsCommand.Execute(null);
            for (var i = 0; i < 300 && viewModel.DownloadVersions.Count == 0; i++) Thread.Sleep(100);

            var target = viewModel.DownloadVersions.FirstOrDefault(item => item.Id == wanted);
            lines.Add("");
            lines.Add($"[下载进度] 版本 {wanted}");

            if (target is null)
            {
                lines.Add("    版本列表里没有该版本");
            }
            else
            {
                viewModel.SelectedDownloadVersion = target;
                viewModel.DownloadInstanceName = "进度测试";

                var samples = new System.Collections.Concurrent.ConcurrentBag<double>();
                using var stop = new CancellationTokenSource();
                var sampler = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        samples.Add(viewModel.DownloadProgress);
                        try { await Task.Delay(150, stop.Token); } catch { }
                    }
                });

                viewModel.DownloadCommand.Execute(null);
                for (var i = 0; i < 600 && viewModel.IsDownloading; i++) Thread.Sleep(200);

                stop.Cancel();
                try { sampler.Wait(2000); } catch { }

                var list = samples.ToArray();
                var nonZero = list.Count(value => value > 0);

                lines.Add($"    采样点 = {list.Length}，非零 {nonZero} 个");
                lines.Add($"    最小 = {list.DefaultIfEmpty(0).Min():N1}%  最大 = {list.DefaultIfEmpty(0).Max():N1}%");
                lines.Add($"    状态 = {viewModel.DownloadStatus}");
                lines.Add($"    滚动态出现过 = {list.Any(value => value == 0)}");
            }
        }
        // 验证"能力检测 + 目录入口可见性"
        if (args.Any(arg => string.Equals(arg, "--exercise-caps", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[能力检测]");

            foreach (var candidate in viewModel.Instances)
            {
                viewModel.SelectedInstance = candidate;
                Thread.Sleep(1500);

                var tabs = string.Join(", ", viewModel.ContentTabs.Select(tab => $"{tab.Title}={(tab.IsEnabled ? "开" : "关")}"));

                lines.Add($"    {candidate.Name}");
                lines.Add($"      加载器 {viewModel.DetectedLoadersText}  光影支持={viewModel.HasShaderSupport}");
                lines.Add($"      入口 mods={viewModel.HasModsDirectory} 资源包={viewModel.HasResourcePacksDirectory} 光影={viewModel.HasShaderPacksDirectory}");
                lines.Add($"      页签 {tabs}");
                lines.Add($"      列表行 {candidate.ContentText}");
                lines.Add($"      存档入口={viewModel.HasSavesDirectory}");
                lines.Add($"      按钮可用 mods={viewModel.OpenModsFolderCommand.CanExecute(null)}"
                          + $" 光影={viewModel.OpenShaderPacksFolderCommand.CanExecute(null)}"
                          + $" 存档={viewModel.OpenSavesFolderCommand.CanExecute(null)}"
                          + $" 删除={viewModel.DeleteInstanceCommand.CanExecute(null)}");
            }
        }
        // 验证"拖入世界"：文件夹 / zip / 嵌套 zip / 非世界包
        if (args.Any(arg => string.Equals(arg, "--exercise-world-import", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[导入世界]");

            var instance = viewModel.Instances.FirstOrDefault(item => item.SavesDirectory.Contains("原版生存"))
                           ?? viewModel.Instances.FirstOrDefault();

            if (instance is not null)
            {
                viewModel.SelectedInstance = instance;
                Thread.Sleep(1200);

                var temp = Path.Combine(Path.GetTempPath(), "nmpc-world-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(temp);

                // 1) 普通布局：level.dat 在根
                var worldDir = Path.Combine(temp, "导入测试A");
                Directory.CreateDirectory(worldDir);
                File.WriteAllBytes(Path.Combine(worldDir, "level.dat"), new byte[] { 1, 2, 3 });

                // 2) 嵌套布局：外层目录里才带 level.dat
                var nestedOuter = Path.Combine(temp, "nested");
                Directory.CreateDirectory(Path.Combine(nestedOuter, "导入测试B"));
                File.WriteAllBytes(Path.Combine(nestedOuter, "导入测试B", "level.dat"), new byte[] { 4, 5, 6 });

                // 3) 非世界包
                var bad = Path.Combine(temp, "bad");
                Directory.CreateDirectory(bad);
                File.WriteAllText(Path.Combine(bad, "readme.txt"), "not a world");

                var zipA = Path.Combine(temp, "导入测试A.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(worldDir, zipA);

                var zipB = Path.Combine(temp, "导入测试B.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(nestedOuter, zipB);

                var zipBad = Path.Combine(temp, "坏包.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(bad, zipBad);

                var before = viewModel.Worlds.Count;

                // 文件夹形态
                viewModel.ImportWorldsAsync(new[] { worldDir }).GetAwaiter().GetResult();
                lines.Add($"    文件夹导入后：{viewModel.Worlds.Count} 个世界（{viewModel.Status}）");

                // zip 形态（根布局）
                viewModel.ImportWorldsAsync(new[] { zipA }).GetAwaiter().GetResult();
                lines.Add($"    zip(根) 导入后：{viewModel.Worlds.Count} 个世界（{viewModel.Status}）");

                // zip 形态（嵌套布局）
                viewModel.ImportWorldsAsync(new[] { zipB }).GetAwaiter().GetResult();
                lines.Add($"    zip(嵌套) 导入后：{viewModel.Worlds.Count} 个世界（{viewModel.Status}）");

                // 非世界包应被拒绝
                viewModel.ImportWorldsAsync(new[] { zipBad }).GetAwaiter().GetResult();
                lines.Add($"    坏包后：{viewModel.Worlds.Count} 个世界（{viewModel.Status}）");

                lines.Add($"    世界名：{string.Join("、", viewModel.Worlds.Select(world => world.Name))}");
                lines.Add($"    净增 {viewModel.Worlds.Count - before} 个");

                // 清理导入的测试世界与临时文件
                foreach (var world in viewModel.Worlds.Where(w => w.Name.StartsWith("导入测试")).ToList())
                {
                    try { Directory.Delete(world.Directory, recursive: true); } catch { }
                }

                try { Directory.Delete(temp, recursive: true); } catch { }
                viewModel.LoadWorldsForTest();
            }
        }
        // 验证"切页签立刻点数据包"这种紧邻操作（以前会把有效扫描判为过期）
        if (args.Any(arg => string.Equals(arg, "--exercise-datapack-race", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[数据包竞态]");

            foreach (var candidate in viewModel.Instances)
            {
                viewModel.SelectedInstance = candidate;
                Thread.Sleep(800);

                var tab = viewModel.ContentTabs.FirstOrDefault(item => item.ContentKind == ContentKind.DataPack);
                if (tab is null) continue;

                // 不等待：模拟用户"点页签马上点数据包"
                viewModel.SelectedTab = tab;
                viewModel.Worlds.FirstOrDefault()?.ManageDataPacksCommand.Execute(null);

                Thread.Sleep(2000);

                lines.Add($"    {candidate.Name}：世界 {viewModel.Worlds.Count} 个，"
                          + $"内容 {viewModel.Content.Count} 个，管理视图={viewModel.IsManagingDataPacks}");
            }
        }
        // 验证可见性随导航/页签变化。
        // 这些属性是派生值，XAML 只在收到 PropertyChanged 时重新求值；
        // 只看 getter 的结果永远是对的，所以必须同时核对"通知有没有发出来"。
        if (args.Any(arg => string.Equals(arg, "--exercise-nav", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[可见性]");

            var target = viewModel.Instances.FirstOrDefault(item => item.Name == "26.1.2")
                         ?? viewModel.Instances.FirstOrDefault();

            if (target is not null)
            {
                viewModel.SelectedInstance = target;
                Thread.Sleep(900);

                // 从概览出发：默认页签是概览，而前面几项诊断可能已经把页签换走了
                viewModel.SelectedTab = viewModel.ContentTabs[0];
                Thread.Sleep(600);

                var announced = new HashSet<string>(StringComparer.Ordinal);
                void OnChanged(object? sender, PropertyChangedEventArgs e) =>
                    announced.Add(e.PropertyName ?? "");

                viewModel.PropertyChanged += OnChanged;

                var expected = new[]
                {
                    nameof(MainWindowViewModel.ShowOverview),
                    nameof(MainWindowViewModel.ShowContentPane),
                    nameof(MainWindowViewModel.ShowContentList),
                    nameof(MainWindowViewModel.ShowLog),
                    nameof(MainWindowViewModel.IsContentEmptyOrNotList),
                };

                void Dump(string tag, bool requireNotifications = true, string[]? extra = null)
                {
                    lines.Add(
                        $"    {tag}：当前页签={viewModel.SelectedTab.Title} 面板={viewModel.ShowContentPane} "
                        + $"头部={viewModel.ShowInstanceHeader} 概览={viewModel.ShowOverview} "
                        + $"列表={viewModel.ShowContentList} 空提示={viewModel.IsContentEmptyOrNotList} "
                        + $"日志={viewModel.ShowLog}");

                    if (!requireNotifications)
                    {
                        lines.Add("        通知：（页签被拒绝切换，无需核对）");
                        announced.Clear();
                        return;
                    }

                    var required = extra is null ? expected : expected.Concat(extra).ToArray();
                    var missing = required.Where(name => !announced.Contains(name)).ToList();
                    lines.Add($"        通知：{(missing.Count == 0 ? "齐全" : "缺 " + string.Join(" / ", missing))}");
                    announced.Clear();
                }

                void Switch(string tag, ContentKind kind)
                {
                    var tab = viewModel.ContentTabs.First(item => item.ContentKind == kind);
                    var enabled = tab.IsEnabled;
                    viewModel.SelectedTab = tab;
                    Thread.Sleep(700);
                    Dump(tag, enabled);
                }

                Switch("模组页签", ContentKind.Mod);
                Switch("资源包页签", ContentKind.ResourcePack);
                Switch("存档页签（世界列表）", ContentKind.DataPack);

                if (viewModel.Worlds.FirstOrDefault() is not null)
                {
                    viewModel.Worlds[0].ManageDataPacksCommand.Execute(null);
                    Thread.Sleep(1500);
                    Dump($"存档页签（数据包，{viewModel.Content.Count} 项）");
                }

                viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.Kind == DetailTabKind.Log);
                Thread.Sleep(700);
                Dump("日志页签");

                viewModel.ShowDownloadsCommand.Execute(null);
                Thread.Sleep(600);
                Dump("下载页", extra: new[]
                {
                    // 进下载页时左侧「游戏本体 / 整合包」的高亮也要跟着通知，
                    // 否则会显示内容却没高亮（首屏进下载页的经典现象）
                    nameof(MainWindowViewModel.VersionsModeBackground),
                    nameof(MainWindowViewModel.VersionsModeForeground),
                    nameof(MainWindowViewModel.ModpacksModeBackground),
                    nameof(MainWindowViewModel.ModpacksModeForeground),
                });

                viewModel.ShowInstancesCommand.Execute(null);
                Thread.Sleep(600);
                Dump("回到实例页");

                viewModel.PropertyChanged -= OnChanged;
            }
        }
        // 验证 Java 候选缓存：同一实例第二次切换应明显更快
        if (args.Any(arg => string.Equals(arg, "--exercise-java-cache", StringComparison.OrdinalIgnoreCase)))
        {
            var cacheA = viewModel.Instances.FirstOrDefault();
            var cacheB = viewModel.Instances.Skip(1).FirstOrDefault();

            if (cacheA is not null && cacheB is not null)
            {
                bool Ready() => viewModel.JavaChoices.Count > 0 && viewModel.SelectedJavaChoice is not null;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                viewModel.SelectedInstance = cacheA;
                for (var i = 0; i < 100 && !Ready(); i++) Thread.Sleep(50);
                var firstMs = watch.ElapsedMilliseconds;

                viewModel.SelectedInstance = cacheB;
                Thread.Sleep(1500);

                watch.Restart();
                viewModel.SelectedInstance = cacheA;
                Thread.Sleep(150);
                var secondMs = watch.ElapsedMilliseconds;

                lines.Add("");
                lines.Add("[Java 缓存]");
                lines.Add($"    首次进入 {cacheA.Name}：{firstMs} ms");
                lines.Add($"    再次进入 {cacheA.Name}：{secondMs} ms（150ms 后已就绪 = 命中缓存）");
                lines.Add($"    候选项 {viewModel.JavaChoices.Count} 个，选中 {viewModel.SelectedJavaChoice?.Label}");
            }
        }
        // 验证数据包页签（按世界）
        if (args.Any(arg => string.Equals(arg, "--exercise-datapacks", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[数据包]");

            foreach (var candidate in viewModel.Instances)
            {
                viewModel.SelectedInstance = candidate;
                Thread.Sleep(1500);

                var tab = viewModel.ContentTabs.FirstOrDefault(item => item.ContentKind == ContentKind.DataPack);
                lines.Add($"    {candidate.Name}（{candidate.VersionId}）");
                lines.Add($"      数据包页签 可用={tab?.IsEnabled}  提示={tab?.TabTip ?? "-"}  页签数字={tab?.DisplayTitle}");
                lines.Add($"      世界 {viewModel.Worlds.Count} 个");

                if (tab is { IsEnabled: true })
                {
                    viewModel.SelectedTab = tab;
                    Thread.Sleep(1200);

                    lines.Add($"      世界列表视图={viewModel.ShowWorldPicker}  管理视图={viewModel.IsManagingDataPacks}");
                    foreach (var world in viewModel.Worlds)
                        lines.Add($"      世界行 {world.Name}  数据包按钮={world.CanManageDataPacks}");

                    // 进入数据包管理（行内按钮）
                    viewModel.Worlds.FirstOrDefault()?.ManageDataPacksCommand.Execute(null);
                    Thread.Sleep(1500);

                    lines.Add($"      → 管理后：{viewModel.DataPackWorld?.Name}  列表视图={viewModel.ShowWorldPicker}");
                    lines.Add($"      → 目录 {viewModel.DataPackWorld?.Directory}\\datapacks");
                    lines.Add($"      → 内容 {viewModel.Content.Count} 个  空提示={viewModel.IsContentEmpty}");

                    // 返回世界列表
                    viewModel.BackToWorldsCommand.Execute(null);
                    Thread.Sleep(300);
                    lines.Add($"      → 返回后：世界列表视图={viewModel.ShowWorldPicker} 管理视图={viewModel.IsManagingDataPacks} 内容 {viewModel.Content.Count} 个");
                    lines.Add($"      → 返回后页签数字={tab.DisplayTitle}");
                }
            }

            // 跨实例切换：先进入"有存档的实例"的世界数据包，再切到"没有存档的实例"。
            // 数字以前会留着上一个实例/世界的数据包数（实例 0 个存档却写「存档 1」）。
            var withWorlds = viewModel.Instances.FirstOrDefault(item =>
                                 item.Name == "26.1.2" || item.SaveCount > 0);
            var withoutWorlds = viewModel.Instances.FirstOrDefault(item => item.SaveCount == 0);

            if (withWorlds is not null && withoutWorlds is not null)
            {
                var tab = viewModel.ContentTabs.First(item => item.ContentKind == ContentKind.DataPack);

                viewModel.SelectedInstance = withWorlds;
                Thread.Sleep(1200);
                viewModel.SelectedTab = tab;
                Thread.Sleep(1200);
                viewModel.Worlds.FirstOrDefault()?.ManageDataPacksCommand.Execute(null);
                Thread.Sleep(1500);
                var whileManaging = tab.DisplayTitle;

                // 仍然停在存档页签，只换实例
                viewModel.SelectedInstance = withoutWorlds;
                Thread.Sleep(1500);

                lines.Add("");
                lines.Add($"[存档数字] {withWorlds.Name}（管理世界时 页签={whileManaging}）"
                          + $" → {withoutWorlds.Name}：世界 {viewModel.Worlds.Count} 个，"
                          + $"页签={tab.DisplayTitle}，管理视图={viewModel.IsManagingDataPacks}");
                lines.Add($"    期望：{withoutWorlds.Name} 没有存档时页签不再显示数字，也不保留上一个实例的世界");
            }
        }
        // 验证实例页的 Java 候选列表
        if (args.Any(arg => string.Equals(arg, "--exercise-java", StringComparison.OrdinalIgnoreCase)))
        {
            var target = viewModel.Instances.FirstOrDefault();
            if (target is not null)
            {
                viewModel.SelectedInstance = target;
                Thread.Sleep(4000);

                lines.Add("");
                lines.Add($"[Java 候选] 实例 {target.Name}");
                lines.Add($"    共 {viewModel.JavaChoices.Count} 个，当前选中：{viewModel.SelectedJavaChoice?.Label}");
                lines.Add($"    实例记录：path={target.JavaPath ?? "(空)"} component={target.JavaComponent ?? "(空)"}");
                lines.Add($"    提示：{viewModel.JavaHint}");

                foreach (var choice in viewModel.JavaChoices)
                    lines.Add($"      · {choice.Label}");
            }
        }
        // 验证版本类型过滤（正式版 / 含快照 / 全部）
        if (args.Any(arg => string.Equals(arg, "--exercise-version-filter", StringComparison.OrdinalIgnoreCase)))
        {
            viewModel.ShowDownloadsCommand.Execute(null);
            for (var i = 0; i < 300 && viewModel.DownloadVersions.Count == 0; i++) Thread.Sleep(100);

            lines.Add("");
            lines.Add("[版本过滤]");

            for (var filter = 0; filter <= 3; filter++)
            {
                viewModel.VersionFilterIndex = filter;

                // 列表重建是 fire-and-forget，等状态行更新
                Thread.Sleep(3500);

                lines.Add($"    {viewModel.VersionFilterText,-26} {viewModel.DownloadVersions.Count,4} 个"
                          + $"   首个 {viewModel.DownloadVersions.FirstOrDefault()?.Id}");

                // 愚人节这一档把每个都列出来，确认年份与主题都在
                if (filter == 3)
                {
                    foreach (var item in viewModel.DownloadVersions)
                        lines.Add($"        · {item.DisplayText}");
                }
            }
        }
        // 验证"实例页/下载页"两个区块的可见性互斥（曾经在实例页也显示下载区块）
        lines.Add("");
        lines.Add($"[导航] 实例页     版本区块={viewModel.IsVersionsMode} "
                  + $"整合包区块={viewModel.IsModpacksMode} 版本列表={viewModel.ShowVersionList}");

        viewModel.ShowDownloadsCommand.Execute(null);
        lines.Add($"[导航] 下载/版本  版本区块={viewModel.IsVersionsMode} "
                  + $"整合包区块={viewModel.IsModpacksMode} 版本列表={viewModel.ShowVersionList}");

        viewModel.ShowModpacksModeCommand.Execute(null);
        lines.Add($"[导航] 下载/整合包 版本区块={viewModel.IsVersionsMode} "
                  + $"整合包区块={viewModel.IsModpacksMode} 版本列表={viewModel.ShowVersionList}");

        // 文件夹被手动改名：负载在目录里的标记把 Id / 显示名同步回启动器
        if (args.Any(arg => string.Equals(arg, "--exercise-instance-folder-sync", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[文件夹改名同步]");

            var paths = new LauncherPaths(root);
            var target = viewModel.Instances.FirstOrDefault(item =>
                             string.Equals(item.Name, item.Id, StringComparison.OrdinalIgnoreCase))
                         ?? viewModel.Instances.FirstOrDefault();

            if (target is null)
            {
                lines.Add("    没有实例");
            }
            else
            {
                var originalId = target.Id;
                var originalDirectory = Path.Combine(paths.Instances, originalId);
                var tempName = "外部改名测试";
                var tempDirectory = Path.Combine(paths.Instances, tempName);

                if (!Directory.Exists(originalDirectory))
                {
                    lines.Add($"    目录不存在：{originalDirectory}");
                }
                else if (Directory.Exists(tempDirectory))
                {
                    lines.Add($"    目标目录已存在，跳过：{tempDirectory}");
                }
                else
                {
                    Directory.Move(originalDirectory, tempDirectory);
                    viewModel.LoadAsync().GetAwaiter().GetResult();

                    var synced = viewModel.Instances.FirstOrDefault(item => item.Id == tempName);
                    lines.Add($"    文件夹 {originalId} → {tempName}："
                              + $"Id={synced?.Id ?? "(丢失)"} 名称={synced?.Name ?? "(丢失)"}");

                    var reloaded = new MainWindowViewModel(paths);
                    reloaded.LoadAsync().GetAwaiter().GetResult();
                    lines.Add($"    重新读取 → {reloaded.Instances.FirstOrDefault(item => item.Id == tempName)?.Name ?? "(丢失)"}");

                    // 还原目录并再次对账
                    Directory.Move(tempDirectory, originalDirectory);
                    viewModel.LoadAsync().GetAwaiter().GetResult();

                    var restored = viewModel.Instances.FirstOrDefault(item => item.Id == originalId);
                    lines.Add($"    还原 → Id={restored?.Id ?? "(丢失)"} 名称={restored?.Name ?? "(丢失)"}");
                }
            }
        }

        // CurseForge 整合包完整安装：搜索 ATM10 → 准备 → 走现有安装流程（下载 1.21.1 + NeoForge + 499 模组）
        if (args.Any(arg => string.Equals(arg, "--exercise-cf-install", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[CurseForge 整合包安装]");
            var cfProgressLog = Path.Combine(root, "cfinstall-progress.log");
            void CfLog(string message) => File.AppendAllText(cfProgressLog, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            CfLog("start");

            if (!CurseForgeClient.TryResolveApiKey(out var cfInstallKey, out var cfInstallProblem)
                || cfInstallKey is null)
            {
                lines.Add($"    key 不可用：{cfInstallProblem}");
            }
            else
            {
                using var cfHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                var cf = new CurseForgeClient(cfHttp, cfInstallKey);

                var hits = cf.SearchAsync("All the Mods 10", null, Array.Empty<string>(), 5, 4471)
                    .GetAwaiter().GetResult();

                var target = hits.FirstOrDefault(hit =>
                                 string.Equals(hit.Slug, "all-the-mods-10", StringComparison.OrdinalIgnoreCase))
                             ?? hits.FirstOrDefault();

                if (target is null)
                {
                    lines.Add("    没搜到 ATM10");
                }
                else
                {
                    var files = cf.GetFilesAsync(target.ModId, "1.21.1", new[] { "neoforge" })
                        .GetAwaiter().GetResult();

                    var file = files.FirstOrDefault(item =>
                        item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                    if (file is null)
                    {
                        lines.Add("    ATM10 没有 zip 文件");
                    }
                    else
                    {
                        lines.Add($"    {target.DisplayName} / {file.FileName}（{file.FileId}）");

                        try
                        {
                            var plan = viewModel.PrepareCurseForgeModpackAsync(target.ModId, file.FileId)
                                .GetAwaiter().GetResult();

                            lines.Add($"    准备完成：{plan.Name} {plan.VersionLabel}"
                                      + (plan.Problems.Count > 0
                                          ? $"（{plan.Problems.Count} 个文件作者禁止 API 下载）"
                                          : "")
                                      + $" / {plan.Index.GameVersion} / {plan.Index.LoaderText}"
                                      + $" / {plan.Index.Files.Count} 个模组");

                            CfLog("install");
                            foreach (var problem in plan.Problems.Take(8))
                                lines.Add($"      跳过：{problem}");
                            viewModel.InstallPreparedModpackAsync().GetAwaiter().GetResult();
                            CfLog("installed");
                            lines.Add($"    安装结果：{viewModel.ModpackStatus}");
                        }
                        catch (Exception ex)
                        {
                            lines.Add($"    安装失败：{ex.GetBaseException().Message}");
                        }
                    }
                }
            }
        }

        if (args.Any(arg => string.Equals(arg, "--exercise-instance-marker", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[实例整合包标记]");

            var marked = viewModel.Instances.FirstOrDefault(item => item.HasModpackInfo);

            if (marked is null)
            {
                lines.Add("    没有带标记的实例");
            }
            else
            {
                viewModel.SelectedInstance = marked;
                Thread.Sleep(1200);

                lines.Add($"    实例：{marked.Name}");
                lines.Add($"    标记：{marked.ModpackSummaryText}");
                lines.Add($"    缺失：{marked.MissingModsCount} 个；面板条目 {viewModel.BlockedMods.Count} 个；显示={viewModel.HasBlockedMods}");
                lines.Add($"    提示：{viewModel.BlockedModsHint}");
            }
        }

        // CurseForge 阻塞文件：只准备、列出 blocked 列表（不安装）
        if (args.Any(arg => string.Equals(arg, "--exercise-cf-blocked", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[CF blocked 列表]");

            if (!CurseForgeClient.TryResolveApiKey(out var blockedKey, out var blockedProblem)
                || blockedKey is null)
            {
                lines.Add($"    key 不可用：{blockedProblem}");
            }
            else
            {
                using var blockedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                var blockedCf = new CurseForgeClient(blockedHttp, blockedKey);
                var blockedHits = blockedCf.SearchAsync("All the Mods 10", null, Array.Empty<string>(), 5, 4471)
                    .GetAwaiter().GetResult();

                var blockedTarget = blockedHits.FirstOrDefault(hit =>
                    string.Equals(hit.Slug, "all-the-mods-10", StringComparison.OrdinalIgnoreCase));

                if (blockedTarget is null)
                {
                    lines.Add("    没搜到 ATM10");
                }
                else
                {
                    var blockedFiles = blockedCf.GetFilesAsync(blockedTarget.ModId, "1.21.1", new[] { "neoforge" })
                        .GetAwaiter().GetResult();

                    var blockedFile = blockedFiles.FirstOrDefault(item =>
                        item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                    if (blockedFile is not null)
                    {
                        var blockedPlan = viewModel.PrepareCurseForgeModpackAsync(
                            blockedTarget.ModId, blockedFile.FileId).GetAwaiter().GetResult();

                        lines.Add($"    blocked {blockedPlan.Blocked.Count} 个：");

                        foreach (var item in blockedPlan.Blocked)
                        {
                            lines.Add($"      {item.DisplayName} / {item.FileName}");
                            lines.Add($"        sha1={item.Sha1}");
                            lines.Add($"        {item.PageUrl}");
                        }
                    }
                }
            }
        }

        if (args.Any(arg => string.Equals(arg, "--exercise-modpack-badge", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[整合包标记]");

            foreach (var item in viewModel.Instances)
            {
                lines.Add($"    {item.Name}："
                          + (item.HasModpackInfo ? item.ModpackSummaryText : "（普通实例）"));
            }
        }

        // 模组页：本地筛选 + 下载模式
        if (args.Any(arg => string.Equals(arg, "--exercise-mod-view", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[模组页视图]");

            var modInstance = viewModel.Instances.FirstOrDefault(item => item.HasModpackInfo)
                              ?? viewModel.Instances.FirstOrDefault();

            if (modInstance is not null)
            {
                viewModel.NavigateForTest(NavPage.Instances);
                viewModel.SelectedInstance = modInstance;
                Thread.Sleep(1000);
                viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
                Thread.Sleep(1000);

                lines.Add($"    列表模式：筛选框={viewModel.IsContentFilterVisible} 列表={viewModel.ShowContentRows} 搜索={viewModel.IsSearchPanelVisible}");

                viewModel.OpenContentDownloadCommand.Execute(null);
                Thread.Sleep(300);
                lines.Add($"    下载模式：筛选框={viewModel.IsContentFilterVisible} 列表={viewModel.ShowContentRows} 搜索={viewModel.IsSearchPanelVisible}");

                viewModel.CloseContentDownloadCommand.Execute(null);
                Thread.Sleep(300);
                lines.Add($"    返回列表：列表={viewModel.ShowContentRows} 搜索={viewModel.IsSearchPanelVisible}");

                viewModel.HidePackMods = false;
                Thread.Sleep(1500);
                viewModel.RefreshContentAsync().GetAwaiter().GetResult();
                lines.Add($"    显示全部后列表 {viewModel.Content.Count} 个（隐藏={viewModel.HidePackMods}）");
                viewModel.ContentFilterQuery = "tombstone";
                Thread.Sleep(1500);
                viewModel.RefreshContentAsync().GetAwaiter().GetResult();
                Thread.Sleep(2500);
                lines.Add($"    本地筛选 tombstone：{viewModel.Content.Count} 个");

                var beforeMem = GC.GetTotalMemory(true);
                for (var i = 0; i < 6; i++) viewModel.HidePackMods = i % 2 == 0;
                viewModel.RefreshContentAsync().GetAwaiter().GetResult();
                var afterMem = GC.GetTotalMemory(true);
                lines.Add($"    反复开关 6 次内存变化：{(afterMem - beforeMem) / 1024} KB；当前列表 {viewModel.Content.Count} 个");
                lines.Add($"    整合包来源归属：CF={viewModel.Content.Count(item => item.IsFromCurseForge)} 个，"
                                    + $"Modrinth={viewModel.Content.Count(item => item.IsFromModrinth)} 个，"
                                    + $"未识别={viewModel.Content.Count(item => item.IsUnidentified)} 个");

                viewModel.ContentFilterQuery = "";
                viewModel.HidePackMods = true;
                viewModel.RefreshContentAsync().GetAwaiter().GetResult();
            }
        }

        // 整合包实例：折叠自带模组 + 只检查手动添加的
        if (args.Any(arg => string.Equals(arg, "--exercise-pack-mods", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[整合包模组折叠]");

            var pack = viewModel.Instances.FirstOrDefault(item => item.HasModpackInfo);

            if (pack is null)
            {
                lines.Add("    没有整合包实例");
            }
            else
            {
                viewModel.SelectedInstance = pack;
                Thread.Sleep(1000);
                viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
                Thread.Sleep(1200);

                lines.Add($"    默认隐藏={viewModel.HidePackMods}  可折叠={viewModel.CanHidePackMods}");
                lines.Add($"    列表 {viewModel.Content.Count} 个；{viewModel.PackModsHint}");

                viewModel.CheckUpdatesAsync().GetAwaiter().GetResult();
                lines.Add($"    无手动模组时检查：{viewModel.Status}");

                var dummy = Path.Combine(pack.ModsDirectory, "zzz-user-mod.jar");
                File.WriteAllText(dummy, "not a real mod");

                try
                {
                    viewModel.RefreshContentAsync().GetAwaiter().GetResult();
                    Thread.Sleep(800);

                    lines.Add($"    加入手动模组后列表 {viewModel.Content.Count} 个");

                    viewModel.CheckUpdatesAsync().GetAwaiter().GetResult();
                    lines.Add($"    更新检查：{viewModel.Status}");
                }
                finally
                {
                    try { File.Delete(dummy); } catch { }
                    viewModel.RefreshContentAsync().GetAwaiter().GetResult();
                }
            }
        }

        // 默认浏览器的下载目录 + 多目录导入
        if (args.Any(arg => string.Equals(arg, "--exercise-browser-dirs", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[下载目录]");
            lines.Add($"    默认浏览器目录：{NPMC.Launcher.Infrastructure.Downloads.BrowserDownloadLocator.GetDefaultBrowserDownloadDirectory() ?? "（未识别）"}");
            lines.Add($"    系统 Downloads：{NPMC.Launcher.Infrastructure.Downloads.BrowserDownloadLocator.GetKnownDownloadsFolder()}");

            foreach (var directory in NPMC.Launcher.Infrastructure.Downloads.BrowserDownloadLocator.GetCandidateDirectories())
                lines.Add($"    监听：{directory}");

            var tempRoot = Path.Combine(Path.GetTempPath(), "nmpc-dirs-" + Guid.NewGuid().ToString("N")[..8]);
            var firstDir = Path.Combine(tempRoot, "browser");
            var secondDir = Path.Combine(tempRoot, "downloads");
            var instanceDir = Path.Combine(tempRoot, "instance");

            try
            {
                Directory.CreateDirectory(firstDir);
                Directory.CreateDirectory(secondDir);
                Directory.CreateDirectory(instanceDir);

                var content = System.Text.Encoding.UTF8.GetBytes("multi-dir " + Guid.NewGuid());
                var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(content)).ToLowerInvariant();

                using var importer = new NPMC.Launcher.Infrastructure.Modpacks.BlockedModImporter(
                    new[] { new NPMC.Launcher.Core.Modpacks.BlockedModFile(1, 2, "Multi", "multi", "Multi.jar", sha1) },
                    instanceDir);

                importer.Watch(new[] { firstDir, secondDir });
                File.WriteAllBytes(Path.Combine(secondDir, "Multi.jar"), content);
                importer.ScanNow();
                Thread.Sleep(1500);

                lines.Add($"    第二个目录的文件被导入：{(File.Exists(Path.Combine(instanceDir, "mods", "Multi.jar")) ? "是" : "否")}");
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        // 被作者禁止下载的文件：模拟浏览器下载 → SHA1 校验 → 导入实例
        if (args.Any(arg => string.Equals(arg, "--exercise-blocked", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[Blocked 模组导入]");

            var tempRoot = Path.Combine(Path.GetTempPath(), "nmpc-blocked-" + Guid.NewGuid().ToString("N")[..8]);
            var watchDir = Path.Combine(tempRoot, "Downloads");
            var instanceDir = Path.Combine(tempRoot, "instance");

            try
            {
                Directory.CreateDirectory(watchDir);
                Directory.CreateDirectory(instanceDir);

                var content = System.Text.Encoding.UTF8.GetBytes("nmpc blocked test " + Guid.NewGuid());
                var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(content))
                    .ToLowerInvariant();

                var blocked = new NPMC.Launcher.Core.Modpacks.BlockedModFile(
                    1, 2, "Test Mod", "test-mod", "TestMod.jar", sha1);

                using var importer = new NPMC.Launcher.Infrastructure.Modpacks.BlockedModImporter(
                    new[] { blocked }, instanceDir, message => lines.Add($"    {message}"));

                importer.Watch(watchDir);

                // 先放一个同名但内容不同的文件：必须被 SHA1 拒绝
                File.WriteAllText(Path.Combine(watchDir, "TestMod.jar"), "wrong content");
                importer.ScanNow();
                Thread.Sleep(1500);

                lines.Add($"    错误文件被拒绝：{(importer.Pending.Count == 1 ? "是" : "否")}");

                File.WriteAllBytes(Path.Combine(watchDir, "TestMod.jar"), content);
                importer.ScanNow();
                Thread.Sleep(2500);

                var destination = Path.Combine(instanceDir, "mods", "TestMod.jar");
                lines.Add($"    正确文件导入：{(File.Exists(destination) ? "是" : "否")}");
                lines.Add($"    待处理剩余：{importer.Pending.Count}");
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        // CurseForge：API key + ATM10 搜索 / 文件 / 下载地址（只读探测，不安装）
        if (args.Any(arg => string.Equals(arg, "--exercise-cf", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[CurseForge]");

            if (!CurseForgeClient.TryResolveApiKey(out var cfKey, out var cfProblem) || cfKey is null)
            {
                lines.Add($"    key 不可用：{cfProblem}");
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var cf = new CurseForgeClient(http, cfKey);

                var hits = cf.SearchAsync("All the Mods 10", gameVersion: null,
                    loaders: Array.Empty<string>(), limit: 5, classId: 4471).GetAwaiter().GetResult();

                lines.Add($"    搜索 ATM10：{hits.Count} 个结果"
                          + (hits.Count > 0 ? $"（首个：{hits[0].DisplayName} id={hits[0].ModId}）" : ""));

                if (hits.Count > 0)
                {
                    var files = cf.GetFilesAsync(hits[0].ModId, "1.21.1", new[] { "neoforge" })
                        .GetAwaiter().GetResult();
                    lines.Add($"    文件 {files.Count} 个"
                              + (files.Count > 0 ? $"（最新：{files[0].FileName} id={files[0].FileId}）" : ""));

                    if (files.Count > 0)
                    {
                        var url = cf.TryGetDownloadUrlAsync(hits[0].ModId, files[0].FileId)
                            .GetAwaiter().GetResult();
                        lines.Add(url is null
                            ? "    下载地址：作者禁止 API 下载"
                            : $"    下载地址：{new Uri(url).Host}/{Path.GetFileName(new Uri(url).AbsolutePath)}");
                    }
                }
            }
        }

        // 整合包在线搜索：Modrinth 搜索 → 选中第一个 → 读取版本列表（不下载）
        if (args.Any(arg => string.Equals(arg, "--exercise-modpack-search", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[整合包在线搜索]");

            var searchArgIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-modpack-search", StringComparison.OrdinalIgnoreCase));

            viewModel.ModpackSearchQuery = searchArgIndex + 1 < args.Length ? args[searchArgIndex + 1] : "fabulously";

            viewModel.SearchModpacksCommand.Execute(null);
            Thread.Sleep(6000);

            lines.Add($"    搜索「{viewModel.ModpackSearchQuery}」：{viewModel.ModpackSearchStatus}");
            lines.Add($"    结果 {viewModel.ModpackHits.Count} 个"
                      + (viewModel.ModpackHits.Count == 0
                          ? ""
                          : $"（首个：{viewModel.ModpackHits[0].Title} · {viewModel.ModpackHits[0].DownloadText}）"));

            if (viewModel.SelectedModpackHit is not null)
            {
                Thread.Sleep(5000);
                lines.Add($"    版本 {viewModel.ModpackVersions.Count} 个"
                          + (viewModel.SelectedModpackVersion is { } version
                              ? $"，默认 {version.DisplayText}"
                              : ""));

                if (viewModel.SelectedModpackVersion is { } picked
                    && args.Any(arg => string.Equals(arg, "--download", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var plan = viewModel
                            .PrepareOnlineModpackAsync(viewModel.SelectedModpackHit.Slug, picked.Id)
                            .GetAwaiter().GetResult();

                        lines.Add($"    下载解析：{plan.Name} / {plan.Index.GameVersion} / {plan.Index.LoaderText}"
                                  + $" / {plan.Index.Files.Count} 个文件 / {plan.Index.TotalBytes / 1024} KB");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"    下载解析失败：{ex.GetBaseException().Message}");
                    }
                }
            }
        }

        // 页签数量要跟实例走：停在「日志」页换实例时，数字不能留在上一个实例上
        if (args.Any(arg => string.Equals(arg, "--exercise-tab-counts", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[页签数量]");

            var items = viewModel.Instances.ToList();

            if (items.Count < 2)
            {
                lines.Add("    实例不足两个，跳过");
            }
            else
            {
                var logTab = viewModel.ContentTabs.First(tab => tab.Kind == DetailTabKind.Log);

                viewModel.SelectedInstance = items[0];
                Thread.Sleep(800);
                viewModel.SelectedTab = logTab;
                Thread.Sleep(300);

                viewModel.SelectedInstance = items[1];
                Thread.Sleep(1500);

                var second = items[1];

                int CountOf(ContentKind kind) =>
                    viewModel.ContentTabs.FirstOrDefault(tab => tab.ContentKind == kind)?.Count ?? -1;

                var tabs = $"mods={CountOf(ContentKind.Mod)} packs={CountOf(ContentKind.ResourcePack)}"
                           + $" shaders={CountOf(ContentKind.ShaderPack)} saves={CountOf(ContentKind.DataPack)}";

                var expected = $"mods={second.ModsCount} packs={second.ResourcePackCount}"
                               + $" shaders={second.ShaderPackCount} saves={second.SaveCount}";

                lines.Add($"    {second.Name}：页签 {tabs}  |  实例 {expected}"
                          + (tabs == expected ? "  ✔" : "  ✘ 不一致"));

                // 再切回第一个实例（同样停在日志页），验证数字不会留在第二个上
                viewModel.SelectedInstance = items[0];
                Thread.Sleep(1500);

                var firstItem = items[0];
                var tabsBack = $"mods={CountOf(ContentKind.Mod)} packs={CountOf(ContentKind.ResourcePack)}"
                               + $" shaders={CountOf(ContentKind.ShaderPack)} saves={CountOf(ContentKind.DataPack)}";
                var expectedBack = $"mods={firstItem.ModsCount} packs={firstItem.ResourcePackCount}"
                                   + $" shaders={firstItem.ShaderPackCount} saves={firstItem.SaveCount}";

                lines.Add($"    {firstItem.Name}：页签 {tabsBack}  |  实例 {expectedBack}"
                          + (tabsBack == expectedBack ? "  ✔" : "  ✘ 不一致"));
            }
        }

        // 实例排序：验证 VM → instances.json → 重新读取 的往返
        if (args.Any(arg => string.Equals(arg, "--exercise-instance-order", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[实例顺序]");

            var store = new JsonInstanceStore(new LauncherPaths(root));
            var originalIds = viewModel.Instances.Select(item => item.Id).ToList();

            if (viewModel.Instances.Count >= 2)
            {
                var topItem = viewModel.Instances[0];
                var bottomItem = viewModel.Instances[^1];

                viewModel.MoveInstance(bottomItem.Id, topItem.Id);
                Thread.Sleep(800);

                lines.Add($"    拖动 {bottomItem.Name} 到第 1 位 → "
                          + string.Join(" | ", viewModel.Instances.Select(item => item.Name)));

                var reloaded = new MainWindowViewModel(new LauncherPaths(root));
                reloaded.LoadAsync().GetAwaiter().GetResult();
                lines.Add($"    重新读取 → {string.Join(" | ", reloaded.Instances.Select(item => item.Name))}");
            }

            // 还原顺序，诊断不该留下痕迹
            var catalog = new InstanceCatalog(store.LoadAsync().GetAwaiter().GetResult());
            catalog.Reorder(originalIds);
            store.SaveAsync(catalog.Instances).GetAwaiter().GetResult();
            lines.Add($"    已还原：{string.Join(" | ", originalIds)}");
        }

        // 实例改名：显示名与目录一起改，最后还原
        if (args.Any(arg => string.Equals(arg, "--exercise-instance-rename", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[实例改名]");

            var target = viewModel.SelectedInstance ?? viewModel.Instances.FirstOrDefault();
            var paths = new LauncherPaths(root);

            if (target is null)
            {
                lines.Add("    没有实例");
            }
            else
            {
                var originalId = target.Id;
                var originalName = target.Name;
                var originalDirectory = Path.Combine(paths.Instances, originalId);

                viewModel.RenameText = "改名测试";
                viewModel.ConfirmRenameCommand.Execute(null);
                Thread.Sleep(1200);

                var newId = target.Id;
                var newDirectory = Path.Combine(paths.Instances, newId);

                lines.Add($"    「{originalName}」→「{target.Name}」");
                lines.Add($"    目录 {originalId} → {newId}"
                          + $"（旧目录还在={Directory.Exists(originalDirectory)}，新目录存在={Directory.Exists(newDirectory)}）");

                var reloaded = new MainWindowViewModel(paths);
                reloaded.LoadAsync().GetAwaiter().GetResult();
                lines.Add($"    重新读取 → {reloaded.Instances.FirstOrDefault(item => item.Id == newId)?.Name ?? "(丢失)"}");

                // 还原：改回原名（目录也会跟着改回去）
                viewModel.RenameText = originalName;
                viewModel.ConfirmRenameCommand.Execute(null);
                Thread.Sleep(1200);

                lines.Add($"    还原 → Id={target.Id} 名称={target.Name}"
                          + $" 目录存在={Directory.Exists(Path.Combine(paths.Instances, target.Id))}");
            }
        }

        // 离线账号：新建 → 设为默认 → 绑定实例 → 启动解析 → 还原并删除临时账号
        if (args.Any(arg => string.Equals(arg, "--exercise-accounts", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[账号]");

            var before = viewModel.Accounts.Select(item => item.Username).ToList();

            // 先点「创建账号」选择离线方式，再填用户名并点创建
            viewModel.CreateAccountCommand.Execute(null);
            Thread.Sleep(200);
            lines.Add($"    空档案：IsCreatingAccount={viewModel.IsCreatingAccount} 账号数={viewModel.Accounts.Count}");

            viewModel.ChooseOfflineAccountCommand.Execute(null);
            viewModel.NewAccountName = "DiagAcc";
            viewModel.ConfirmOfflineAccountCommand.Execute(null);
            Thread.Sleep(1400);

            var created = viewModel.Accounts.FirstOrDefault(item => item.Username == "DiagAcc");
            lines.Add($"    新建：{created?.Username ?? "(失败)"} UUID={created?.UuidDisplay ?? "-"}"
                      + $"  草稿已收={!viewModel.IsCreatingAccount}");

            if (created is not null)
            {
                viewModel.SelectedAccount = created;
                viewModel.SetDefaultAccountCommand.Execute(null);
                Thread.Sleep(500);
                lines.Add($"    默认：Selected={viewModel.SelectedAccountIsDefault} "
                          + $"设置下拉={viewModel.SettingsDefaultAccountChoice?.Display ?? "-"}");

                // 第二个账号 + 默认切换：徽标应该从上一个账号搬到新账号
                viewModel.CreateAccountCommand.Execute(null);
                Thread.Sleep(200);
                viewModel.ChooseOfflineAccountCommand.Execute(null);
                viewModel.NewAccountName = "DiagAcc2";
                viewModel.ConfirmOfflineAccountCommand.Execute(null);
                Thread.Sleep(1400);

                var second = viewModel.Accounts.FirstOrDefault(item => item.Username == "DiagAcc2");

                if (second is not null)
                {
                    viewModel.SelectedAccount = second;
                    Thread.Sleep(200);
                    viewModel.SetDefaultAccountCommand.Execute(null);
                    Thread.Sleep(700);

                    lines.Add("    默认切换：" + string.Join("、", viewModel.Accounts.Select(item =>
                        $"{item.Username}={(item.IsDefault ? "默认" : "-")}")));
                }

                if (viewModel.SelectedInstance is { } instance)
                {
                    var choice = viewModel.AccountOptions.FirstOrDefault(option => option.Id == created.Id);
                    viewModel.SelectedAccountChoice = choice;
                    Thread.Sleep(800);

                    lines.Add($"    实例 {instance.Name} 的 AccountId={instance.Model.Settings.AccountId}");

                    var resolved = viewModel.ResolveLaunchAccountAsync(instance.Model).GetAwaiter().GetResult();
                    lines.Add($"    启动解析 → {resolved?.Username ?? "null"} / {resolved?.Uuid ?? "-"}");
                }

                // 还原：实例改回"跟随默认"
                if (viewModel.SelectedInstance is not null && viewModel.AccountOptions.Count > 0)
                {
                    viewModel.SelectedAccountChoice = viewModel.AccountOptions[0];
                    Thread.Sleep(600);
                }

                // 删除临时账号（两次点击确认）
                viewModel.SelectedAccount = viewModel.Accounts.FirstOrDefault(item => item.Username == "DiagAcc");
                viewModel.DeleteAccountCommand.Execute(null);
                Thread.Sleep(250);
                viewModel.DeleteAccountCommand.Execute(null);
                Thread.Sleep(900);

                viewModel.SelectedAccount = viewModel.Accounts.FirstOrDefault(item => item.Username == "DiagAcc2");
                viewModel.DeleteAccountCommand.Execute(null);
                Thread.Sleep(250);
                viewModel.DeleteAccountCommand.Execute(null);
                Thread.Sleep(900);

                lines.Add($"    清理：剩余账号 = {string.Join("、", viewModel.Accounts.Select(item => item.Username))}"
                          + $"（原有：{string.Join("、", before)}）");

                if (viewModel.SelectedInstance is { } after)
                    lines.Add($"    还原后实例 AccountId={after.Model.Settings.AccountId ?? "null"}");
            }

            // 空名取消：打开草稿但不填用户名，取消后账号数不变
            var countBeforeCancel = viewModel.Accounts.Count;
            viewModel.CreateAccountCommand.Execute(null);
            Thread.Sleep(300);
            viewModel.CancelCreateAccountCommand.Execute(null);
            Thread.Sleep(300);

            lines.Add($"    空名取消：账号数 {countBeforeCancel} → {viewModel.Accounts.Count}，"
                      + $"草稿已收={!viewModel.IsCreatingAccount}");

            // 搜索过滤
            viewModel.AccountSearchText = "zzz-不存在的账号";
            Thread.Sleep(250);
            lines.Add($"    搜索不存在的名字：列表 {viewModel.Accounts.Count} 个（应为 0）");

            viewModel.AccountSearchText = "";
            Thread.Sleep(250);
            lines.Add($"    清空搜索：列表 {viewModel.Accounts.Count} 个");
        }

        // 皮肤：内置选择与"同名正版皮肤"（需要网络；失败也应当优雅提示）
        if (args.Any(arg => string.Equals(arg, "--exercise-skin", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[皮肤]");

            var name = "Notch";

            viewModel.CreateAccountCommand.Execute(null);
            Thread.Sleep(200);
            viewModel.ChooseOfflineAccountCommand.Execute(null);
            viewModel.NewAccountName = name;
            viewModel.ConfirmOfflineAccountCommand.Execute(null);
            Thread.Sleep(1400);

            var account = viewModel.Accounts.FirstOrDefault(item => item.Username == name);

            if (account is null)
            {
                lines.Add("    临时账号创建失败");
            }
            else
            {
                viewModel.SelectedAccount = account;
                Thread.Sleep(300);

                lines.Add($"    默认皮肤={account.Account.SkinText}");

                viewModel.SelectedSkinIndex = 3; // 使用同名正版皮肤
                Thread.Sleep(300);
                viewModel.FetchMojangSkinCommand.Execute(null);

                for (var i = 0; i < 60 && viewModel.IsFetchingSkin; i++) Thread.Sleep(200);
                Thread.Sleep(500);

                var fetched = viewModel.SelectedAccount?.Account;
                lines.Add($"    取回：皮肤={fetched?.SkinText ?? "-"} 模型={fetched?.SkinModel ?? "-"}"
                          + $" 文件存在={(fetched?.SkinPath is { } path && File.Exists(path))}");

                // 切到自定义（不选文件）再切回 Alex，确认能保存/预览
                viewModel.SelectedSkinIndex = 2;
                Thread.Sleep(400);
                lines.Add($"    自定义：手臂={viewModel.SelectedSkinArmIndex} 提示={viewModel.SkinHint}");

                viewModel.SelectedSkinIndex = 1; // Alex
                Thread.Sleep(400);
                var alex = viewModel.SelectedAccount?.Account;
                lines.Add($"    Alex：皮肤={alex?.SkinText} 路径={(alex?.SkinPath is null ? "null" : "有")}");

                // 清理：删除临时账号
                viewModel.SelectedAccount = viewModel.Accounts.FirstOrDefault(item => item.Username == name);
                viewModel.DeleteAccountCommand.Execute(null);
                Thread.Sleep(250);
                viewModel.DeleteAccountCommand.Execute(null);
                Thread.Sleep(800);

                lines.Add($"    清理：剩余={string.Join("、", viewModel.Accounts.Select(item => item.Username))}");
            }
        }

        // 加载器检查的缓存与"切回不再卡住"：首次联网耗时 vs 切回命中缓存耗时
        if (args.Any(arg => string.Equals(arg, "--exercise-loader-cache", StringComparison.OrdinalIgnoreCase)))
        {
            var cacheIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-loader-cache", StringComparison.OrdinalIgnoreCase));
            var minecraft = cacheIndex + 1 < args.Length ? args[cacheIndex + 1] : "26.3";

            lines.Add("");
            lines.Add($"[加载器缓存] {minecraft}");

            viewModel.ShowDownloadsCommand.Execute(null);
            for (var i = 0; i < 300 && viewModel.DownloadVersions.Count == 0; i++) Thread.Sleep(100);

            var item = viewModel.DownloadVersions.FirstOrDefault(version => version.Id == minecraft);

            if (item is null)
            {
                lines.Add($"    版本清单里没有 {minecraft}");
            }
            else
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                viewModel.SelectedDownloadVersion = item;

                for (var i = 0; i < 900 && viewModel.IsCheckingLoaders; i++) Thread.Sleep(100);

                lines.Add($"    首次：{watch.ElapsedMilliseconds} ms  检查中={viewModel.IsCheckingLoaders}  "
                          + $"Fabric={viewModel.CanSelectFabric} Forge={viewModel.CanSelectForge} "
                          + $"NeoForge={viewModel.CanSelectNeoForge}");

                var other = viewModel.DownloadVersions.FirstOrDefault(version => version.Id != minecraft);

                if (other is not null)
                {
                    viewModel.SelectedDownloadVersion = other;
                    for (var i = 0; i < 900 && viewModel.IsCheckingLoaders; i++) Thread.Sleep(100);

                    watch.Restart();
                    viewModel.SelectedDownloadVersion = item;

                    // 切回时应当在几十毫秒内从缓存恢复（不联网）
                    for (var i = 0; i < 900 && viewModel.IsCheckingLoaders; i++) Thread.Sleep(100);

                    lines.Add($"    切回：{watch.ElapsedMilliseconds} ms  检查中={viewModel.IsCheckingLoaders}  "
                              + $"Fabric={viewModel.CanSelectFabric} Forge={viewModel.CanSelectForge} "
                              + $"NeoForge={viewModel.CanSelectNeoForge}");
                }

                lines.Add($"    缓存文件={viewModel.LoaderCachePath}"
                          + $"（存在={File.Exists(viewModel.LoaderCachePath)}）");
            }
        }

        // 走界面同一条路：下载本体 → 装加载器 → 建实例
        //   --exercise-download-loader <mc> [fabric|forge|neoforge] [loaderVersion]
        if (args.Any(arg => string.Equals(arg, "--exercise-download-loader", StringComparison.OrdinalIgnoreCase)))
        {
            var loaderIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-download-loader", StringComparison.OrdinalIgnoreCase));
            var minecraft = loaderIndex + 1 < args.Length ? args[loaderIndex + 1] : "1.20.1";
            var kindText = loaderIndex + 2 < args.Length ? args[loaderIndex + 2] : "forge";
            var wantedLoader = loaderIndex + 3 < args.Length ? args[loaderIndex + 3] : null;

            var kind = kindText.ToLowerInvariant() switch
            {
                "fabric" => LoaderKind.Fabric,
                "neoforge" => LoaderKind.NeoForge,
                _ => LoaderKind.Forge,
            };

            lines.Add("");
            lines.Add($"[下载 + {kind}] {minecraft}");

            viewModel.ShowDownloadsCommand.Execute(null);
            for (var i = 0; i < 300 && viewModel.DownloadVersions.Count == 0; i++) Thread.Sleep(100);

            var item = viewModel.DownloadVersions.FirstOrDefault(version => version.Id == minecraft);

            if (item is null)
            {
                lines.Add($"    版本清单里没有 {minecraft}");
            }
            else
            {
                viewModel.SelectedDownloadVersion = item;

                // 三家加载器并行查，等检查结束
                for (var i = 0; i < 600 && viewModel.IsCheckingLoaders; i++) Thread.Sleep(100);

                lines.Add($"    Fabric={viewModel.CanSelectFabric} Forge={viewModel.CanSelectForge} NeoForge={viewModel.CanSelectNeoForge}");
                lines.Add($"    提示：{viewModel.LoaderHint}");

                viewModel.SelectedLoader = kind;

                var versions = kind switch
                {
                    LoaderKind.Fabric => viewModel.FabricLoaderVersions.Select(loader => loader.Version).ToList(),
                    LoaderKind.NeoForge => viewModel.NeoForgeLoaderVersions.Select(loader => loader.Loader).ToList(),
                    _ => viewModel.ForgeLoaderVersions.Select(loader => loader.Loader).ToList(),
                };

                var chosen = (wantedLoader is null ? null : versions.FirstOrDefault(v => v == wantedLoader))
                             ?? versions.FirstOrDefault();

                if (chosen is null)
                {
                    lines.Add($"    {kind} 加载器清单为空（{viewModel.DownloadStatus}）");
                }
                else
                {
                    if (kind == LoaderKind.Fabric)
                    {
                        viewModel.SelectedFabricLoader = viewModel.FabricLoaderVersions
                            .First(loader => loader.Version == chosen);
                    }
                    else if (kind == LoaderKind.NeoForge)
                    {
                        viewModel.SelectedNeoForgeLoader = viewModel.NeoForgeLoaderVersions
                            .First(loader => loader.Loader == chosen);
                    }
                    else
                    {
                        viewModel.SelectedForgeLoader = viewModel.ForgeLoaderVersions
                            .First(loader => loader.Loader == chosen);
                    }

                    lines.Add($"    加载器 {chosen}  实例名 {viewModel.DownloadInstanceName}");

                    viewModel.DownloadCommand.Execute(null);

                    for (var i = 0; i < 900 && viewModel.IsDownloading; i++) Thread.Sleep(500);

                    var created = viewModel.Instances.FirstOrDefault(instance =>
                        instance.Name == viewModel.DownloadInstanceName);

                    lines.Add($"    状态：{viewModel.DownloadStatus}");
                    lines.Add($"    实例「{viewModel.DownloadInstanceName}」存在={created is not null}"
                              + $"  版本={created?.VersionId}");
                }
            }
        }

        // Forge / NeoForge 加载器清单（maven-metadata.xml）：只读清单，不下载安装包
        if (args.Any(arg => string.Equals(arg, "--exercise-forge-versions", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("");
            lines.Add("[Forge / NeoForge 版本清单]");

            foreach (var minecraft in new[] { "26.3", "26.1.2", "1.20.1", "1.21.6" })
            {
                try
                {
                    var forge = viewModel.ListForgeVersionsAsync(minecraft).GetAwaiter().GetResult();
                    var neo = viewModel.ListNeoForgeVersionsAsync(minecraft).GetAwaiter().GetResult();

                    lines.Add($"    {minecraft}：Forge {forge.Count} 个"
                              + (forge.Count == 0
                                  ? ""
                                  : $"（最新 [{forge[0].ChannelBadge}] {forge[0].Loader}）")
                              + $"  NeoForge {neo.Count} 个"
                              + (neo.Count == 0
                                  ? ""
                                  : $"（最新 [{neo[0].ChannelBadge}] {neo[0].Loader}）"));
                }
                catch (Exception ex)
                {
                    lines.Add($"    {minecraft}：读取失败 {ex.GetBaseException().Message}");
                }
            }
        }

        // 只组装启动计划、不真跑：核对命令行（尤其 Forge 1.17+ 的 -p 模块路径与占位符）        if (args.Any(arg => string.Equals(arg, "--exercise-launch-plan", StringComparison.OrdinalIgnoreCase)))
        {
            var planIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-launch-plan", StringComparison.OrdinalIgnoreCase));
            var requested = planIndex >= 0 && planIndex + 1 < args.Length ? args[planIndex + 1] : null;

            var target = (requested is null
                             ? null
                             : viewModel.Instances.FirstOrDefault(item => item.Name == requested))
                         ?? viewModel.Instances.FirstOrDefault(item =>
                             item.VersionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
                         ?? viewModel.Instances.FirstOrDefault();

            lines.Add("");
            lines.Add("[启动计划]");

            if (target is null)
            {
                lines.Add("    没有实例");
            }
            else
            {
                var paths = new LauncherPaths(root);
                var version = GameLauncher.TryLoadInstalledVersionAsync(paths, target.VersionId)
                    .GetAwaiter().GetResult();

                if (version is null)
                {
                    lines.Add($"    {target.Name}：版本 {target.VersionId} 未安装");
                }
                else
                {
                    var prepared = GameLauncher.Prepare(
                        paths, target.Model, version, new JsonSettingsStore(paths).Load());

                    var unresolved = LaunchPlanFactory.FindUnresolvedPlaceholders(prepared.Plan);

                    lines.Add($"    实例={target.Name}  版本={version.Id}  根版本={version.RootVersionId ?? "(自身)"}");
                    lines.Add($"    mainClass={version.MainClass}");
                    lines.Add($"    java={prepared.Plan.JavaPath}");
                    lines.Add($"    未替换占位符={(unresolved.Count == 0 ? "无" : string.Join(", ", unresolved))}");
                    lines.Add($"    JVM 参数 {prepared.Plan.JvmArguments.Count} 个 / 游戏参数 {prepared.Plan.GameArguments.Count} 个");

                    foreach (var problem in prepared.Problems) lines.Add($"    问题: {problem}");

                    // -p / -cp 里列出的文件是否都在 —— Forge 的模块路径少一个 jar 就起不来
                    var missing = new List<string>();
                    var jvm = prepared.Plan.JvmArguments;

                    for (var i = 0; i + 1 < jvm.Count; i++)
                    {
                        if (jvm[i] is not ("-p" or "-cp" or "--module-path" or "--class-path")) continue;

                        foreach (var entry in jvm[i + 1].Split(Path.PathSeparator,
                                     StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!File.Exists(entry)) missing.Add(entry);
                        }
                    }

                    foreach (var entry in missing.Take(8)) lines.Add($"    缺文件: {entry}");
                    lines.Add($"    -p/-cp 缺失 {missing.Count} 个");
                }
            }
        }

        // 真跑一次启动（界面同一条路径），确认游戏能起来、锁文件到位
        if (args.Any(arg => string.Equals(arg, "--exercise-launch", StringComparison.OrdinalIgnoreCase)))
        {
            // 允许点名实例：--exercise-launch <实例名>
            var launchIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--exercise-launch", StringComparison.OrdinalIgnoreCase));
            var requestedName = launchIndex >= 0 && launchIndex + 1 < args.Length ? args[launchIndex + 1] : null;

            var target = (requestedName is null
                             ? null
                             : viewModel.Instances.FirstOrDefault(item => item.Name == requestedName))
                         ?? viewModel.Instances.FirstOrDefault(item =>
                             item.VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                         ?? viewModel.Instances.FirstOrDefault();

            if (target is not null)
            {
                viewModel.SelectedInstance = target;
                Thread.Sleep(1000);

                var lockFile = Path.Combine(target.GameDirectory, ".nmpc-running");
                if (File.Exists(lockFile)) File.Delete(lockFile);

                viewModel.LaunchGameCommand.Execute(null);

                Thread.Sleep(45000);

                // 先读状态再杀进程：杀了之后必然变成"异常退出"，那是我造成的
                var statusBeforeKill = viewModel.Status;
                var running = System.Diagnostics.Process.GetProcessesByName("java").Length;
                var logFile = Path.Combine(target.GameDirectory, "logs", "latest.log");
                var logTail = ReadTailSafe(logFile, 3);

                lines.Add("");
                lines.Add($"[启动] 实例 {target.Name}");
                lines.Add($"    状态(杀之前) = {statusBeforeKill}");
                lines.Add($"    java 进程 = {running} 个");
                lines.Add($"    锁文件   = {File.Exists(lockFile)}");
                lines.Add($"    日志末尾 = {logTail}");

                foreach (var process in System.Diagnostics.Process.GetProcessesByName("java"))
                {
                    try { process.Kill(true); } catch { }
                }

                Thread.Sleep(1500);
                lines.Add($"    清理后锁 = {File.Exists(lockFile)}");
            }
        }
        // Java 候选在"选中实例"时异步加载，等它落定再报告
        Thread.Sleep(2500);
        lines.Add("");
        lines.Add($"[Java] 候选 {viewModel.JavaChoices.Count} 个，当前 {viewModel.SelectedJavaChoice?.Label ?? "(无)"}, 提示={viewModel.JavaHint}");

        File.WriteAllLines(outputPath, lines);
        return true;
    }

    private static string CurrentDirectory(MainWindowViewModel viewModel, ContentKind kind)
        => viewModel.SelectedInstance?.DirectoryFor(kind) ?? "(无选中实例)";

    private static string Describe(string? value) => string.IsNullOrWhiteSpace(value) ? "(空)" : value;
}
