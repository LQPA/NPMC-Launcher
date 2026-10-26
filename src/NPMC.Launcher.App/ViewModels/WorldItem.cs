using System.Windows.Input;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 存档里的一个世界。
///
/// 行内操作直接挂在世界对象上（而不是绑定到"选中的世界"），
/// 这样每个世界一行就能独立操作，也不需要为命令的可用性做额外刷新 ——
/// 之前在页签上方放一个统一按钮，就踩过"命令停在旧的禁用态"的坑。
/// </summary>
public sealed class WorldItem
{
    public WorldItem(
        string name,
        string directory,
        DateTime lastPlayed,
        bool canManageDataPacks,
        Action<WorldItem> openFolder,
        Action<WorldItem> manageDataPacks,
        Action<WorldItem> delete)
    {
        Name = name;
        Directory = directory;
        LastPlayed = lastPlayed;
        CanManageDataPacks = canManageDataPacks;

        OpenFolderCommand = new RelayCommand(() => openFolder(this));
        ManageDataPacksCommand = new RelayCommand(() => manageDataPacks(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    /// <summary>世界文件夹名（游戏内显示名的兜底）。</summary>
    public string Name { get; }

    /// <summary>世界目录的完整路径。</summary>
    public string Directory { get; }

    public DateTime LastPlayed { get; }

    public string LastPlayedText => LastPlayed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>数据包是 1.13 才有的功能；低版本也允许管理存档，只是没有这个按钮。</summary>
    public bool CanManageDataPacks { get; }

    public ICommand OpenFolderCommand { get; }
    public ICommand ManageDataPacksCommand { get; }
    public ICommand DeleteCommand { get; }

    public override string ToString() => Name;
}
