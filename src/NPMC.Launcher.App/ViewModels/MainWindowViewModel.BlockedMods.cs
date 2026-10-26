using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Infrastructure.Modpacks;

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

    public string Sha1 => File.Sha1;

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

    public ObservableCollection<BlockedModItem> BlockedMods { get; } = new();

    public bool HasBlockedMods => BlockedMods.Count > 0;

    public string BlockedModsHint
    {
        get;
        private set;
    } = "";

    public ICommand OpenBlockedModsCommand { get; private set; }
    public ICommand RecheckBlockedModsCommand { get; private set; }

    private void InitializeBlockedModCommands()
    {
        OpenBlockedModsCommand = new RelayCommand(OpenBlockedModPages);
        RecheckBlockedModsCommand = new RelayCommand(() => _blockedImporter?.ScanNow());
    }

    private void OpenBlockedModPages()
    {
        foreach (var item in BlockedMods)
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.PageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                BlockedModsHint = $"打开浏览器失败：{ex.GetBaseException().Message}";
                OnPropertyChanged(nameof(BlockedModsHint));
                return;
            }
        }

        BlockedModsHint = "已在浏览器打开下载页；下载完成后会自动校验 SHA1 并放进实例。";
        OnPropertyChanged(nameof(BlockedModsHint));
    }

    /// <summary>用户选择自定义下载目录时调用（默认监听系统“下载”目录）。</summary>
    public void SetBlockedDownloadFolder(string directory)
    {
        _blockedImporter?.Watch(directory);
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

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        _blockedImporter.Watch(downloads);

        UpdateBlockedStatus($"有 {BlockedMods.Count} 个文件被作者禁止第三方下载，已开始监听 {downloads}");
    }

    private void UpdateBlockedStatus(string message)
    {
        BlockedModsHint = message;
        OnPropertyChanged(nameof(BlockedModsHint));

        if (_blockedImporter is null) return;

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
