namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 存档里的一个世界。数据包是按世界安装的，所以界面上先选世界。
/// </summary>
public sealed class WorldItem
{
    public WorldItem(string name, string directory, DateTime lastPlayed)
    {
        Name = name;
        Directory = directory;
        LastPlayed = lastPlayed;
    }

    /// <summary>世界文件夹名（也是游戏内显示名的兜底）。</summary>
    public string Name { get; }

    /// <summary>世界目录的完整路径。</summary>
    public string Directory { get; }

    public DateTime LastPlayed { get; }

    public string LastPlayedText => LastPlayed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>下拉里显示：名称 + 最后游玩时间。</summary>
    public string DisplayText => $"{Name}   （最后游玩 {LastPlayedText}）";

    public override string ToString() => DisplayText;
}
