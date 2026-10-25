using NPMC.Launcher.Core.Content;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 列表里的一个实例。
///
/// 内容数量**必须和内容页签用同一套扫描逻辑**（<see cref="ContentScanner"/>）：
/// 之前这里用 "目录下文件数"，会把文件夹形式的资源包漏掉，
/// 于是左侧显示「资源包 1」而页签显示 2 —— 用户会当成 bug。
/// 因此改为可刷新对象，内容变化后由 ViewModel 调用 <see cref="RefreshContentCounts"/>。
/// </summary>
public sealed class InstanceListItem : ObservableObject
{
    private readonly InstanceLayout _layout;
    private int _modsCount;
    private int _resourcePackCount;
    private int _shaderPackCount;
    private int _saveCount;

    public InstanceListItem(LauncherPaths launcher, LauncherInstance instance)
    {
        Model = instance;
        _layout = new InstanceLayout(launcher, instance);

        GameDirectory = _layout.GameDirectory;
        ModsDirectory = _layout.Mods;
        ResourcePacksDirectory = _layout.ResourcePacks;
        ShaderPacksDirectory = _layout.ShaderPacks;
        SavesDirectory = _layout.Saves;

        RefreshContentCounts();
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

    public string CreatedText => Model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string GameDirectory { get; }
    public string ModsDirectory { get; }
    public string ResourcePacksDirectory { get; }
    public string ShaderPacksDirectory { get; }
    public string SavesDirectory { get; }

    public int ModsCount { get => _modsCount; private set => SetProperty(ref _modsCount, value); }
    public int ResourcePackCount { get => _resourcePackCount; private set => SetProperty(ref _resourcePackCount, value); }
    public int ShaderPackCount { get => _shaderPackCount; private set => SetProperty(ref _shaderPackCount, value); }
    public int SaveCount { get => _saveCount; private set => SetProperty(ref _saveCount, value); }

    public string ContentText =>
        $"mods {ModsCount} · 资源包 {ResourcePackCount} · 光影 {ShaderPackCount} · 存档 {SaveCount}";

    /// <summary>某个内容类型对应的目录。</summary>
    public string DirectoryFor(ContentKind kind) => kind switch
    {
        ContentKind.Mod => ModsDirectory,
        ContentKind.ResourcePack => ResourcePacksDirectory,
        _ => ShaderPacksDirectory,
    };

    /// <summary>重新统计各内容目录与存档数。启用/禁用/删除后调用。</summary>
    public void RefreshContentCounts()
    {
        ModsCount = ContentScanner.Scan(ModsDirectory, ContentKind.Mod).Count;
        ResourcePackCount = ContentScanner.Scan(ResourcePacksDirectory, ContentKind.ResourcePack).Count;
        ShaderPackCount = ContentScanner.Scan(ShaderPacksDirectory, ContentKind.ShaderPack).Count;

        SaveCount = Directory.Exists(SavesDirectory)
            ? Directory.EnumerateDirectories(SavesDirectory).Count()
            : 0;

        OnPropertyChanged(nameof(ContentText));
    }
}
