using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Infrastructure.Modpacks;
using NPMC.Launcher.Infrastructure.Downloads;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>界面上的一行“需要去 CurseForge 手动下载”的文件。</summary>
public sealed class BlockedModItem : ObservableObject
{
    private string _status = "等待下载";

    public BlockedModItem(BlockedModFile file)
    {
        File = file;
    }

    public BlockedModFile File { get; }

    public string Name => File.DisplayName;

    public string FileName => File.FileName;

    public string PageUrl => File.PageUrl;
    public string ProjectUrl => File.ProjectUrl;

    public string Sha1 => File.Sha1;
    public string TargetText => "→ " + File.TargetFolder;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

/// <summary>
/// 被 CurseForge 作者禁止 API 下载的模组：打开网页 + 监听下载目录 + SHA1 校验导入。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private BlockedModImporter? _blockedImporter;
    private string? _blockedGameDirectory;

    public ObservableCollection<BlockedModItem> BlockedMods { get; } = new();

    public bool HasBlockedMods => BlockedMods.Count > 0;

    public string BlockedModsHint
    {
        get;
        private set;
    } = "";

    public ICommand OpenBlockedModsCommand { get; private set; }
    public ICommand OpenBlockedProjectPagesCommand { get; private set; }
    public ICommand RecheckBlockedModsCommand { get; private set; }
    public ICommand IgnoreBlockedModsCommand { get; private set; }

    private void InitializeBlockedModCommands()
    {
        OpenBlockedModsCommand = new RelayCommand(() => OpenBlockedPages(useProjectPage: false));
        OpenBlockedProjectPagesCommand = new RelayCommand(() => OpenBlockedPages(useProjectPage: true));
        RecheckBlockedModsCommand = new RelayCommand(() => _blockedImporter?.ScanNow());
        IgnoreBlockedModsCommand = new RelayCommand(IgnoreBlockedMods);
    }

    private void OpenBlockedPages(bool useProjectPage)
    {
        foreach (var item in BlockedMods)
        {
            try
            {
                var url = useProjectPage && !string.IsNullOrWhiteSpace(item.ProjectUrl)
                    ? item.ProjectUrl
                    : item.PageUrl;

                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                BlockedModsHint = $"打开浏览器失败：{ex.GetBaseException().Message}";
                OnPropertyChanged(nameof(BlockedModsHint));
                return;
            }
        }

        BlockedModsHint = useProjectPage
            ? "已打开项目主页：在 Files 里找最新兼容版本；下载后自动校验 SHA1。"
            : "已打开具体文件页；如果 404，可改用「打开全部项目页」。";

        OnPropertyChanged(nameof(BlockedModsHint));
    }

    /// <summary>文件确实已被作者删除、拿不到时：清空缺失列表，不再反复提示。</summary>
    private void IgnoreBlockedMods()
    {
        if (_blockedGameDirectory is not { } directory) return;
        if (InstanceModpackStore.Load(directory) is not { } info) return;

        _blockedImporter?.Dispose();
        _blockedImporter = null;

        InstanceModpackStore.Save(directory, info with { Missing = Array.Empty<BlockedModFile>() });

        BlockedMods.Clear();
        OnPropertyChanged(nameof(HasBlockedMods));

        BlockedModsHint = "已忽略缺失文件（不再提示）；重新安装整合包可以恢复列表。";
        OnPropertyChanged(nameof(BlockedModsHint));

        foreach (var instance in Instances)
        {
            if (string.Equals(instance.GameDirectory, directory, StringComparison.OrdinalIgnoreCase))
                instance.RefreshModpackInfo();
        }
    }

    /// <summary>用户选择自定义下载目录时调用（默认监听系统“下载”目录）。</summary>
    public void SetBlockedDownloadFolder(string directory)
    {
        _blockedImporter?.Watch(directory);
    }

    /// <summary>实例里已有的文件是否与 CurseForge 清单的 SHA1 一致。</summary>
    internal static bool BlockedFileMatches(string gameDirectory, NPMC.Launcher.Core.Modpacks.BlockedModFile file)
    {
        try
        {
            var path = Path.Combine(gameDirectory, file.TargetFolder, file.FileName);
            if (!File.Exists(path) || string.IsNullOrWhiteSpace(file.Sha1)) return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream)).ToLowerInvariant();
            return string.Equals(hash, file.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
    /// <summary>选中实例时，把该实例持久化的缺失列表加载出来（安装页之外也能随时补）。</summary>
    private void LoadBlockedModsForSelectedInstance(InstanceListItem? item)
    {
        _blockedImporter?.Dispose();
        _blockedImporter = null;
        _blockedGameDirectory = null;
        BlockedMods.Clear();

        if (item is null || !item.HasMissingMods)
        {
            OnPropertyChanged(nameof(HasBlockedMods));
            BlockedModsHint = "";
            OnPropertyChanged(nameof(BlockedModsHint));
            return;
        }

        var info = InstanceModpackStore.Load(item.GameDirectory);
        if (info is null || info.Missing.Count == 0)
        {
            OnPropertyChanged(nameof(HasBlockedMods));
            return;
        }

        _blockedGameDirectory = item.GameDirectory;
        _blockedImporter = new BlockedModImporter(
            info.Missing, item.GameDirectory,
            message => Dispatcher.UIThread.Post(() => UpdateBlockedStatus(message)));

        foreach (var file in info.Missing)
            BlockedMods.Add(new BlockedModItem(file));

        OnPropertyChanged(nameof(HasBlockedMods));

        var directories = BrowserDownloadLocator.GetCandidateDirectories();
        _blockedImporter.Watch(directories);

        UpdateBlockedStatus($"缺少 {BlockedMods.Count} 个文件，可随时在 CurseForge 下载后自动导入"
                            + $"（监听 {directories.Count} 个下载目录）");
    }
    /// <summary>安装完成后调用：把 blocked 列表显示出来并开始监听下载目录。</summary>
    private void ShowBlockedMods(ModpackPlan plan, string instanceDirectory)
    {
        if (plan.Blocked.Count == 0) return;

        _blockedImporter?.Dispose();
        _blockedImporter = new BlockedModImporter(
            plan.Blocked, instanceDirectory,
            message => Dispatcher.UIThread.Post(() => UpdateBlockedStatus(message)));

        BlockedMods.Clear();
        foreach (var file in plan.Blocked)
            BlockedMods.Add(new BlockedModItem(file));

        OnPropertyChanged(nameof(HasBlockedMods));

        var directories = BrowserDownloadLocator.GetCandidateDirectories();
        _blockedImporter.Watch(directories);

        UpdateBlockedStatus($"有 {BlockedMods.Count} 个文件被作者禁止第三方下载（可能是资源包/光影），"
                            + $"已开始监听 {directories.Count} 个下载目录：{string.Join("；", directories)}");
    }

    private void UpdateBlockedStatus(string message)
    {
        BlockedModsHint = message;
        OnPropertyChanged(nameof(BlockedModsHint));

        if (_blockedImporter is null) return;

        if (_blockedGameDirectory is { } directory && InstanceModpackStore.Load(directory) is { } info)
        {
            var importedFiles = _blockedImporter.Imported.ToList();
            var packFiles = info.PackFiles.ToList();

            // 手动补齐的文件也是整合包自带文件，不是用户后加的模组：
            // 写进 PackFiles，否则「隐藏整合包自带模组」会把它当成用户模组。
            foreach (var file in importedFiles)
            {
                if (packFiles.All(entry =>
                        !string.Equals(entry.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    packFiles.Add(new PackFileEntry(file.FileName, file.Sha1));
                }
            }

            InstanceModpackStore.Save(directory, info with
            {
                Missing = _blockedImporter.Pending,
                PackFiles = packFiles,
            });

            foreach (var instance in Instances)
            {
                if (string.Equals(instance.GameDirectory, directory, StringComparison.OrdinalIgnoreCase))
                    instance.RefreshModpackInfo();
            }
        }

        var imported = _blockedImporter.Imported.ToList();
        foreach (var item in BlockedMods)
        {
            if (imported.Any(file =>
                    string.Equals(file.Sha1, item.Sha1, StringComparison.OrdinalIgnoreCase)))
            {
                item.Status = "✔ 已导入";
            }
        }
    }
}
