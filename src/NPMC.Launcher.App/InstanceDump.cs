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
        if (first is not null)
        {
            var paths = new LauncherPaths(root);
            var version = GameLauncher.TryLoadInstalledVersionAsync(paths, first.VersionId)
                .GetAwaiter().GetResult();

            lines.Add("");
            if (version is null)
            {
                lines.Add($"[启动准备] 版本 {first.VersionId} 未安装（缺少 version json）");
            }
            else
            {
                var prepared = GameLauncher.Prepare(paths, first.Model, version, new LauncherSettings());

                lines.Add($"[启动准备] 可启动={prepared.CanLaunch}");
                lines.Add($"    java       = {prepared.Plan.JavaPath}");
                lines.Add($"    memory     = {prepared.Plan.JvmArguments.FirstOrDefault(a => a.StartsWith("-Xmx", StringComparison.Ordinal))}");
                lines.Add($"    workingDir = {prepared.Plan.WorkingDirectory}");
                lines.Add($"    jvmArgs    = {prepared.Plan.JvmArguments.Count}");
                lines.Add($"    gameArgs   = {prepared.Plan.GameArguments.Count}");
                lines.Add($"    latestLog  = {prepared.LatestLog}");

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
            viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
            viewModel.CheckUpdatesAsync().GetAwaiter().GetResult();

            lines.Add("");
            lines.Add($"[升级检查] 可升级={viewModel.UpdateCount}");
            lines.Add($"    status = {viewModel.Status}");

            foreach (var item in viewModel.Content)
                lines.Add($"    {(item.HasUpdate ? "可升级" : "已最新")}  {item.Name,-28} {item.UpdateText}");
        }

        File.WriteAllLines(outputPath, lines);
        return true;
    }

    private static string CurrentDirectory(MainWindowViewModel viewModel, ContentKind kind)
        => viewModel.SelectedInstance?.DirectoryFor(kind) ?? "(无选中实例)";
}
