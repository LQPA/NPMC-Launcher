using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App.Diagnostics;

/// <summary>
/// 临时诊断：把窗口"搜索到结果 + 依赖计划可见"的画面离屏渲染成 PNG，用来核对布局有没有重叠。
/// 窗口放到屏幕外、不进任务栏，避免打扰桌面。
/// 用法：--render-search-panel &lt;输出.png&gt; [--render-instance 实例名] [--render-query 关键词]
/// </summary>
internal static class PanelRenderProbe
{
    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--render-search-panel", StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var output = Option(args, "--render-search-panel") ?? Path.Combine(Path.GetTempPath(), "npmc-panel.png");
        var instanceName = Option(args, "--render-instance");
        var query = Option(args, "--render-query") ?? "sodium";
        var height = int.TryParse(Option(args, "--render-height"), out var parsedHeight) ? parsedHeight : 0;
        var fontFamily = Option(args, "--render-font");

        if (height > 0) window.Height = height;

        // 离屏渲染不会做字体回退：Inter 没有中文字形，不指定中文字体的话
        // 中文会画成方框（宽度也算成 0，反倒让文字叠在一起）。只影响这张截图。
        if (!string.IsNullOrWhiteSpace(fontFamily))
            window.FontFamily = new Avalonia.Media.FontFamily(fontFamily);

        try
        {
            var vm = window.DataContext as MainWindowViewModel;

            if (vm is null)
            {
                File.WriteAllText(output + ".error.txt", "DataContext 不是 MainWindowViewModel");
                return;
            }

            var target = (instanceName is null
                             ? null
                             : vm.Instances.FirstOrDefault(item => item.Name == instanceName))
                         ?? vm.Instances.FirstOrDefault();

            if (target is not null)
            {
                vm.SelectedInstance = target;
                await vm.RefreshCapabilitiesAsync(target);
            }

            vm.SelectedTab = vm.ContentTabs.First(tab => tab.ContentKind == ContentKind.Mod);
            vm.OpenModDownloadCommand.Execute(null);

            await vm.SearchForDiagnosticsAsync(query);

            if (!args.Any(arg => string.Equals(arg, "--render-no-plan", StringComparison.OrdinalIgnoreCase))
                && vm.SearchResults.FirstOrDefault() is { } hit)
                await vm.PrepareInstallForDiagnosticsAsync(hit);

            // 让布局与异步图标任务跑完
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                Dispatcher.UIThread.RunJobs();
            }

            window.UpdateLayout();

            var root = window.Content as Control;
            if (root is null)
            {
                File.WriteAllText(output + ".error.txt", "窗口没有 Content");
                return;
            }

            var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Round(root.Bounds.Width * scaling)),
                Math.Max(1, (int)Math.Round(root.Bounds.Height * scaling)));

            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(root);
            bitmap.Save(output);

            File.WriteAllText(output + ".ok.txt",
                $"size={size.Width}x{size.Height} plan={vm.IsPlanVisible} "
                + $"results={vm.SearchResults.Count} expanded={vm.IsSearchPanelExpanded}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(output + ".error.txt", ex.ToString());
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
