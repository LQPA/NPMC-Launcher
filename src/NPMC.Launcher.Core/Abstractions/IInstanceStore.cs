using NPMC.Launcher.Core.Instances;

namespace NPMC.Launcher.Core.Abstractions;

/// <summary>
/// 实例列表的持久化。实现方可以是 JSON 文件、SQLite 或内存（测试用）。
/// Core 只依赖这个接口，不关心落盘格式。
/// </summary>
public interface IInstanceStore
{
    Task<IReadOnlyList<LauncherInstance>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<LauncherInstance> instances, CancellationToken cancellationToken = default);
}
