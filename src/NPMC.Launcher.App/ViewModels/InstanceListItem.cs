using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 列表里的一个实例。
///
/// 保持**不可变**：内容在构造时从 LauncherInstance + InstanceLayout 算好，
/// 界面只负责显示。这样不需要在条目上做属性通知，刷新时整表重建即可。
/// </summary>
public sealed class InstanceListItem
{
    public InstanceListItem(LauncherPaths launcher, LauncherInstance instance)
    {
        Model = instance;

        var layout = new InstanceLayout(launcher, instance);
        GameDirectory = layout.GameDirectory;
        ModsDirectory = layout.Mods;
        SavesDirectory = layout.Saves;

        ModsCount = layout.ListFiles(layout.Mods).Count;
        ResourcePackCount = layout.ListFiles(layout.ResourcePacks).Count;
        ShaderPackCount = layout.ListFiles(layout.ShaderPacks).Count;
        SaveCount = Directory.Exists(layout.Saves)
            ? Directory.EnumerateDirectories(layout.Saves).Count()
            : 0;
    }

    public LauncherInstance Model { get; }

    public string Id => Model.Id;
    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.Id : Model.Name;
    public string VersionId => Model.VersionId;

    public string IsolationText => Model.Isolation switch
    {
        IsolationLevel.None => "不隔离",
        IsolationLevel.Full => "完全隔离",
        _ => "版本隔离",
    };

    public string MemoryText => $"{Model.Settings.MemoryMb} MB";

    public string WindowText => Model.Settings.Fullscreen
        ? "全屏"
        : $"{Model.Settings.WindowWidth}×{Model.Settings.WindowHeight}";

    public string SummaryText => $"{VersionId} · {IsolationText} · {MemoryText}";

    public string ContentText => $"mods {ModsCount} · 资源包 {ResourcePackCount} · 光影 {ShaderPackCount} · 存档 {SaveCount}";

    public string CreatedText => Model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string GameDirectory { get; }
    public string ModsDirectory { get; }
    public string SavesDirectory { get; }
    public int ModsCount { get; }
    public int ResourcePackCount { get; }
    public int ShaderPackCount { get; }
    public int SaveCount { get; }
}
