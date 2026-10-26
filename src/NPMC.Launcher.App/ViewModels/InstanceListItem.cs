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
///
/// 设置（内存 / 窗口）可在概览页直接编辑，编辑后由 <see cref="ApplySettings"/> 就地更新，
/// 不重建列表，避免选中项丢失。
/// </summary>
public sealed class InstanceListItem : ObservableObject
{
    private readonly InstanceLayout _layout;
    private LauncherInstance _model;

    private int _modsCount;
    private int _resourcePackCount;
    private int _shaderPackCount;
    private int _saveCount;

    public InstanceListItem(LauncherPaths launcher, LauncherInstance instance)
    {
        _model = instance;
        _layout = new InstanceLayout(launcher, instance);

        GameDirectory = _layout.GameDirectory;
        ModsDirectory = _layout.Mods;
        ResourcePacksDirectory = _layout.ResourcePacks;
        ShaderPacksDirectory = _layout.ShaderPacks;
        SavesDirectory = _layout.Saves;

        RefreshContentCounts();
    }

    public LauncherInstance Model => _model;

    public string Id => _model.Id;

    /// <summary>实例指定的 java.exe（为空则由版本元数据决定）。</summary>
    public string? JavaPath => _model.Settings.JavaPath;

    /// <summary>实例指定的运行时组件（启动器自带的）。</summary>
    public string? JavaComponent => _model.Settings.JavaComponent;
    public string Name => string.IsNullOrWhiteSpace(_model.Name) ? _model.Id : _model.Name;
    public string VersionId => _model.VersionId;

    public string IsolationText => _model.Isolation switch
    {
        IsolationLevel.None => "不隔离",
        IsolationLevel.Full => "完全隔离",
        _ => "版本隔离",
    };

    public int MemoryMb => _model.Settings.MemoryMb;
    public int WindowWidth => _model.Settings.WindowWidth;
    public int WindowHeight => _model.Settings.WindowHeight;

    public string MemoryText => $"{MemoryMb} MB";

    public string WindowText => _model.Settings.Fullscreen
        ? "全屏"
        : $"{WindowWidth}×{WindowHeight}";

    public string SummaryText => $"{VersionId} · {IsolationText} · {MemoryText}";

    public string CreatedText => _model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string GameDirectory { get; }
    public string ModsDirectory { get; }
    public string ResourcePacksDirectory { get; }
    public string ShaderPacksDirectory { get; }
    public string SavesDirectory { get; }

    public int ModsCount { get => _modsCount; private set => SetProperty(ref _modsCount, value); }
    public int ResourcePackCount { get => _resourcePackCount; private set => SetProperty(ref _resourcePackCount, value); }
    public int ShaderPackCount { get => _shaderPackCount; private set => SetProperty(ref _shaderPackCount, value); }
    public int SaveCount { get => _saveCount; private set => SetProperty(ref _saveCount, value); }

    /// <summary>
    /// 该实例是否有模组加载器。列表里每项都要判断，所以只看版本号
    /// （fabric-loader-… / *Forge* / neoforge / quilt），不做文件 IO。
    /// </summary>
    public bool HasModLoader =>
        VersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase)
        || VersionId.Contains("forge", StringComparison.OrdinalIgnoreCase)
        || VersionId.Contains("quilt", StringComparison.OrdinalIgnoreCase);

    private bool _hasShaderProvider;

    /// <summary>mods 里是否有 Iris / OptiFine：没有的话光影这一栏对用户没有意义。</summary>
    public bool HasShaderProvider => _hasShaderProvider;

    /// <summary>
    /// 列表第二行的内容统计。
    ///
    /// 只显示"这个实例用得上"的项：没有加载器就不显示 mods，
    /// 没有 Iris/OptiFine 就不显示光影 —— 否则原版实例会一直挂着
    /// "mods 0 · 光影 0"，看起来像是坏了。
    /// </summary>
    public string ContentText
    {
        get
        {
            var parts = new List<string>();

            if (HasModLoader) parts.Add($"mods {ModsCount}");
            parts.Add($"资源包 {ResourcePackCount}");
            if (HasShaderProvider) parts.Add($"光影 {ShaderPackCount}");
            parts.Add($"存档 {SaveCount}");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>某个内容类型对应的目录。</summary>
    public string DirectoryFor(ContentKind kind) => kind switch
    {
        ContentKind.Mod => ModsDirectory,
        ContentKind.ResourcePack => ResourcePacksDirectory,
        _ => ShaderPacksDirectory,
    };

    /// <summary>就地替换设置并通知相关显示属性（不重建列表，选中项不受影响）。</summary>
    public void ApplySettings(InstanceSettings settings)
    {
        _model = _model with { Settings = settings };

        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(MemoryMb));
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(WindowText));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>重新统计各内容目录与存档数。启用/禁用/删除后调用。</summary>
    public void RefreshContentCounts()
    {
        ModsCount = ContentScanner.Scan(ModsDirectory, ContentKind.Mod).Count;
        ResourcePackCount = ContentScanner.Scan(ResourcePacksDirectory, ContentKind.ResourcePack).Count;
        ShaderPackCount = ContentScanner.Scan(ShaderPacksDirectory, ContentKind.ShaderPack).Count;

        SaveCount = Directory.Exists(SavesDirectory)
            ? Directory.EnumerateDirectories(SavesDirectory).Count()
            : 0;

        // 光影支持只看有没有 Iris / OptiFine（整合包常放在 mods/<版本>/ 子目录里，要递归）
        try
        {
            _hasShaderProvider = Directory.Exists(ModsDirectory)
                && Directory.EnumerateFiles(ModsDirectory, "*.jar", SearchOption.AllDirectories)
                    .Any(path =>
                    {
                        var name = Path.GetFileName(path);
                        return name.Contains("iris", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("optifine", StringComparison.OrdinalIgnoreCase);
                    });
        }
        catch
        {
            _hasShaderProvider = false;
        }

        OnPropertyChanged(nameof(HasShaderProvider));
        OnPropertyChanged(nameof(ContentText));
    }
}
