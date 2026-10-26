using NPMC.Launcher.Core.Accounts;

namespace NPMC.Launcher.Core.Abstractions;

/// <summary>账号列表的持久化抽象（Core 不关心落盘格式）。</summary>
public interface IAccountStore
{
    Task<IReadOnlyList<PlayerAccount>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<PlayerAccount> accounts, CancellationToken cancellationToken = default);
}
