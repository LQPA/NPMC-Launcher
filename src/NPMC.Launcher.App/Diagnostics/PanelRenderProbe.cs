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

    /// <summary>
    /// 离屏渲染要用的中文字体。**必须在窗口第一次布局之前**设上去：
    /// 布局之后再换字体会让已经量好的文本沿用旧字体度量，中文叠成一团。
    /// </summary>
    public static string? FontOverride(string[] args) => Option(args, "--render-font");

    public static async Task RunAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var output = Option(args, "--render-search-panel") ?? Path.Combine(Path.GetTempPath(), "npmc-panel.png");
        var instanceName = Option(args, "--render-instance");
        var query = Option(args, "--render-query") ?? "sodium";
        var height = int.TryParse(Option(args, "--render-height"), out var parsedHeight) ? parsedHeight : 0;
        var kind = ParseKind(Option(args, "--render-kind"));

        if (height > 0) window.Height = height;

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

            // --render-export：直接渲染导出页（整合包表单），不碰内容页签
            if (args.Any(arg => string.Equals(arg, "--render-export", StringComparison.OrdinalIgnoreCase)))
            {
                vm.SelectedTab = vm.ContentTabs.First(tab => tab.Kind == DetailTabKind.Export);
                vm.ExportFormatIndex = 1;

                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(100);
                    Dispatcher.UIThread.RunJobs();
                }

                window.UpdateLayout();
                RenderTo(window, output, "export");

                File.WriteAllText(output + ".ok.txt",
                    $"size=export tab={vm.SelectedTab.Title} format={vm.ExportFormatIndex} "
                    + $"name={vm.ExportPackName} hint={vm.ExportSelectionHint}");
                return;
            }

            vm.SelectedTab = vm.ContentTabs.First(tab => tab.ContentKind == kind);

            // 数据包按世界存放：先进入一个世界，下载入口与安装目录才有依据
            if (kind == ContentKind.DataPack)
            {
                vm.SelectedWorld = vm.Worlds.FirstOrDefault();
                vm.ManageDataPacksCommand.Execute(null);

                for (var i = 0; i < 60 && !vm.IsManagingDataPacks; i++)
                {
                    await Task.Delay(100);
                    Dispatcher.UIThread.RunJobs();
                }
            }

            // --render-list：只看"列表模式"（本地列表 + 筛选框 + 下载入口），不进下载模式
            if (!args.Any(arg => string.Equals(arg, "--render-list", StringComparison.OrdinalIgnoreCase)))
            {
                vm.OpenContentDownloadCommand.Execute(null);

                await vm.SearchForDiagnosticsAsync(query);

                if (!args.Any(arg => string.Equals(arg, "--render-no-plan", StringComparison.OrdinalIgnoreCase))
                    && vm.SearchResults.FirstOrDefault() is { } hit)
                    await vm.PrepareInstallForDiagnosticsAsync(hit);
            }

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

            RenderTo(window, output, "panel");

            File.WriteAllText(output + ".ok.txt",
                $"size={root.Bounds.Width}x{root.Bounds.Height} kind={kind} tab={vm.SelectedTab.Title} "
                + $"download={vm.IsContentDownloadMode} plan={vm.IsPlanVisible} "
                + $"results={vm.SearchResults.Count}");
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

    /// <summary>把窗口内容渲染成 PNG（离屏，不需要显示器）。</summary>
    private static void RenderTo(MainWindow window, string output, string label)
    {
        var root = window.Content as Control ?? throw new InvalidOperationException("窗口没有 Content");
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(root.Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Round(root.Bounds.Height * scaling)));

        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(root);
        bitmap.Save(output);
    }

    private static ContentKind ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        "resourcepack" or "resource-pack" or "pack" or "resourcepacks" => ContentKind.ResourcePack,
        "shader" or "shaders" or "shaderpack" or "shaderpacks" => ContentKind.ShaderPack,
        "datapack" or "datapacks" => ContentKind.DataPack,
        _ => ContentKind.Mod,
    };

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
