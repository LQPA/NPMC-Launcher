namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 本启动器进程里正在运行的实例集合（按实例 Id）。
///
/// 单启动器多实例：不同实例可以同时启动，同一个实例仍然只允许一个游戏进程
/// （GameLauncher.Start 里还有一层按游戏目录的 .nmpc-running 文件锁）。
/// 用独立的集合而不是一个全局 bool，是因为界面上的「启动/删除/改名/复制」
/// 都应当只跟随"当前选中的这个实例"是否在运行，而不是"有没有任何游戏在运行"。
/// </summary>
public sealed class RunningInstances
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>登记一个实例；已经登记过返回 false（表示该实例正在运行）。</summary>
    public bool TryAdd(string instanceId)
    {
        lock (_gate) return _ids.Add(instanceId);
    }

    public void Remove(string instanceId)
    {
        lock (_gate) _ids.Remove(instanceId);
    }

    public bool Contains(string instanceId)
    {
        lock (_gate) return _ids.Contains(instanceId);
    }

    public int Count
    {
        get
        {
            lock (_gate) return _ids.Count;
        }
    }

    /// <summary>当前运行中的实例 Id 快照（按登记顺序无关紧要）。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return _ids.ToList();
    }
}
