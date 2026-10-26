using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using NPMC.Launcher.App.Diagnostics;
using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App;

/// <summary>
/// 真实窗口下的内存探测（--exercise-memory）。
///
/// 为什么不能用 --dump-instances：那套诊断不初始化 Avalonia 渲染平台，
/// Bitmap 根本解码不出来，量到的内存里没有图标。这里走真实窗口（最小化），
/// 按用户报的路径操作 ViewModel：
///
///   ATM10 → 模组页 → 显示全部 495 个 →（等图标加载）→ 切到 26.1.2 → 概览
///
/// 每个节点记一行：工作集 / 私有字节 / 托管堆 / GC 次数 / 存活位图数 / 图标像素字节。
/// 之后再做"强制 GC → trim 工作集 → Dispose 全部位图"三步对照，用来区分：
///   1. 真泄漏（强制 GC 后位图仍然活着）
///   2. 还没回收（强制 GC 后位图归零，但工作集没还）
///   3. 工作集虚高（trim 后明显下降，私有字节不变）
///
/// 用法：
///   dotnet src\NPMC.Launcher.App\bin\Release\net8.0\NPMC.Launcher.App.dll ^
///     --root .data --exercise-memory [--memory-out .data\memory-probe.txt]
/// </summary>
internal static class MemoryProbe
{
    private const string Option = "--exercise-memory";

    public static bool IsRequested(string[]? args) =>
        args?.Any(arg => string.Equals(arg, Option, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool WantsVisibleWindow(string[]? args) =>
        args?.Any(arg => string.Equals(arg, "--memory-visible", StringComparison.OrdinalIgnoreCase)) == true;

    public static async Task RunAsync(
        MainWindow window,
        IClassicDesktopStyleApplicationLifetime desktop,
        string root,
        string[] args)
    {
        var output = ResolveOutputPath(root, args);
        var lines = new List<string>();
        var gate = new object();

        void Write(string text)
        {
            lock (gate)
            {
                lines.Add(text);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.AppendAllText(output, text + Environment.NewLine);
                }
                catch
                {
                    // 报告写不出去也不能卡住进程
                }
            }

            Console.WriteLine(text);
        }

        var viewModel = window.ViewModel;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, "");

            Write($"# NPMC Launcher 内存探测 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Write($"# root={root}");
            Write($"# pid={Environment.ProcessId} runtime={Environment.Version} os={Environment.OSVersion}");
            Write("");

            await window.LoadTask.WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(1200);

            Snapshot("baseline");

            var atm = viewModel.Instances.FirstOrDefault(item =>
                          item.Id.Contains("All-the-Mods", StringComparison.OrdinalIgnoreCase))
                      ?? viewModel.Instances.FirstOrDefault(item => item.HasModpackInfo);

            if (atm is not null)
            {
                Write("");
                Write($"# —— ATM10 模组页：{atm.Id} ——");

                viewModel.SelectedInstance = atm;
                await Task.Delay(1000);

                viewModel.SelectedTab = viewModel.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
                await Task.Delay(1000);

                // 默认是折叠整合包自带模组；改成"显示全部"才会出现 495 行
                viewModel.HidePackMods = false;
                await Task.Delay(1500);
                await viewModel.RefreshContentAsync();

                Write($"# 内容 {viewModel.Content.Count} 个，等待图标/识别任务…");
                await viewModel.WaitForContentMetadataAsync(TimeSpan.FromSeconds(240));
                await Task.Delay(800);

                // 虚拟化核对：真实窗口里应该只materialize十几行，而不是 495 行
                var rows = window.GetVisualDescendants()
                    .OfType<Control>()
                    .Count(control => control.DataContext is ContentEntryItem && control is Grid);
                var images = window.GetVisualDescendants()
                    .OfType<Image>()
                    .Count(image => image.Source is not null);

                Write($"# 可视树里的内容行={rows}，有 Source 的 Image={images}");
                Snapshot("atm10-mods-all");
                Snapshot("atm10-mods-all+gc", forceCollect: true);

                // 模拟"滚完整张列表"：每次跳 20 行，让虚拟化真正回收/重建行容器。
                if (WantsVisibleWindow(args) && rows > 0)
                {
                    Write("# 程序化滚动整张列表（不是鼠标操作；每次滚一屏，模拟真实浏览）…");

                    for (var index = 0; index < viewModel.Content.Count; index += 8)
                    {
                        window.ContentList.ScrollIntoView(index);
                        await Task.Delay(90);

                        // 每 120 行记一次，用来看工作集是不是随滚动无限上涨
                        if (index > 0 && index % 120 == 0) Snapshot($"scroll-{index}");
                    }

                    await Task.Delay(1500);
                    var lastRows = window.GetVisualDescendants()
                        .OfType<Control>()
                        .Count(control => control.DataContext is ContentEntryItem && control is Grid);

                    Write($"# 滚到底后：可视树内容行={lastRows}，创建位图累计={LiveBitmapTracker.Snapshot().Created}");
                    Snapshot("atm10-scrolled-all");
                    Snapshot("atm10-scrolled-all+gc", forceCollect: true);

                    // 再滚回顶部：验证"已 Dispose 的位图不会被复用中的行容器渲染"（这里崩了就是释放过头）
                    Write("# 滚回顶部（验证释放后的图标能重新从磁盘缓存加载）…");
                    for (var index = viewModel.Content.Count - 1; index >= 0; index -= 8)
                    {
                        window.ContentList.ScrollIntoView(index);
                        await Task.Delay(60);
                    }

                    await Task.Delay(800);
                    Snapshot("atm10-scrolled-back");
                }
            }
            else
            {
                Write("# 没找到整合包实例，跳过 ATM10 步骤");
            }

            var plain = viewModel.Instances.FirstOrDefault(item =>
                            string.Equals(item.Id, "26.1.2", StringComparison.OrdinalIgnoreCase))
                        ?? viewModel.Instances.FirstOrDefault(item =>
                            item.Id.Contains("26.1.2", StringComparison.OrdinalIgnoreCase))
                        ?? viewModel.Instances.FirstOrDefault(item => !ReferenceEquals(item, atm));

            if (plain is null)
            {
                Write("# 没有可切换的第二个实例");
                return;
            }

            Write("");
            Write($"# —— 切换到 {plain.Id} → 概览 ——");

            Snapshot("before-switch");
            viewModel.SelectedInstance = plain;
            Snapshot("after-switch-release");

            // 用户的实际顺序：换实例后还在模组页签，再点「概览」
            viewModel.SelectedTab = viewModel.ContentTabs[0];
            await Task.Delay(2000);
            await viewModel.WaitForContentMetadataAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(800);

            Snapshot("plain-overview");
            Snapshot("plain-overview+gc", forceCollect: true);

            MemorySnapshot.TrimWorkingSet();
            await Task.Delay(500);
            Snapshot("plain-overview+gc+trim");

            // 对照实验：把还活着的位图全部 Dispose，再看一次 —
            // 如果工作集明显下降，就证明"大块内存是位图本身"，而不是托管对象泄漏。
            var disposed = LiveBitmapTracker.DisposeAll();
            Write($"# 已 Dispose 存活位图 {disposed} 个");
            Snapshot("after-dispose+gc", forceCollect: true);

            MemorySnapshot.TrimWorkingSet();
            await Task.Delay(500);
            Snapshot("after-dispose+gc+trim");

            Write("");
            Write("# 结论提示：");
            Write("#   before/after 对比看 switch-release 有没有把位图引用放掉；");
            Write("#   +gc 后 live 仍很大 = 真泄漏；归零但 ws 不降 = 未还给系统；");
            Write("#   +gc+trim 后 ws 大降 = 工作集虚高；after-dispose 再降 = 位图是主因。");
        }
        catch (Exception ex)
        {
            Write($"# 探测失败：{ex.GetType().Name}: {ex.GetBaseException().Message}");
        }
        finally
        {
            Write("# done");
            try { desktop.Shutdown(); } catch { Environment.Exit(0); }
        }

        void Snapshot(string label, bool forceCollect = false)
        {
            Write(viewModel.CaptureMemory(label, forceCollect));
        }
    }

    private static string ResolveOutputPath(string root, string[] args)
    {
        var index = Array.FindIndex(args, arg =>
            string.Equals(arg, "--memory-out", StringComparison.OrdinalIgnoreCase));

        var path = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine(root, "memory-probe.txt");

        return Path.GetFullPath(path);
    }
}
