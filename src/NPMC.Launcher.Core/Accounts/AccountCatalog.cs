namespace NPMC.Launcher.Core.Accounts;

/// <summary>
/// 账号集合的纯逻辑操作（不做 I/O，持久化交给 <see cref="Abstractions.IAccountStore"/>）。
/// 用户名在启动器里唯一（不区分大小写），避免两个"Steve"让人分不清。
/// </summary>
public sealed class AccountCatalog
{
    private readonly List<PlayerAccount> _accounts;

    public AccountCatalog(IEnumerable<PlayerAccount>? accounts = null)
        => _accounts = (accounts ?? Enumerable.Empty<PlayerAccount>()).ToList();

    /// <summary>保持文件里的顺序（账号页按创建顺序展示）。</summary>
    public IReadOnlyList<PlayerAccount> Accounts => _accounts;

    public int Count => _accounts.Count;

    public PlayerAccount? Find(string? id) => id is null
        ? null
        : _accounts.FirstOrDefault(account => string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));

    public PlayerAccount? FindByUsername(string? username) => username is null
        ? null
        : _accounts.FirstOrDefault(account =>
            string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));

    public PlayerAccount AddOffline(string username)
    {
        var name = username.Trim();

        if (!PlayerAccount.IsValidUsername(name))
            throw new ArgumentException("用户名只能是 1~16 个字母、数字或下划线。", nameof(username));

        if (FindByUsername(name) is not null)
            throw new InvalidOperationException($"已经有名为「{name}」的账号了");

        var account = PlayerAccount.CreateOffline(name);
        _accounts.Add(account);
        return account;
    }

    /// <summary>
    /// 登录成功后创建/更新微软账号。同名离线账号会被升级成微软账号，
    /// 保留 Id 与实例绑定；已有同 UUID 的账号则只更新显示名。
    /// </summary>
    public PlayerAccount UpsertMicrosoft(string username, string uuid)
    {
        var name = username.Trim();
        var normalizedUuid = uuid.Replace("-", "").ToLowerInvariant();

        if (!PlayerAccount.IsValidUsername(name))
            throw new ArgumentException("微软返回的玩家名不合法", nameof(username));
        if (normalizedUuid.Length != 32 || !normalizedUuid.All(Uri.IsHexDigit))
            throw new ArgumentException("微软返回的 UUID 不合法", nameof(uuid));

        var byUuid = _accounts.FirstOrDefault(account =>
            string.Equals(account.Uuid, normalizedUuid, StringComparison.OrdinalIgnoreCase));

        if (byUuid is not null)
        {
            var updated = byUuid with
            {
                Username = name,
                Kind = AccountKind.Microsoft,
                Skin = AccountSkin.MojangName,
                SkinPath = null,
                SkinModel = null,
            };

            _accounts[_accounts.IndexOf(byUuid)] = updated;
            return updated;
        }

        var byName = FindByUsername(name);

        if (byName is not null)
        {
            if (byName.Kind == AccountKind.Authlib)
                throw new InvalidOperationException($"已有同名的第三方账号「{name}」");

            var upgraded = byName with
            {
                Username = name,
                Uuid = normalizedUuid,
                Kind = AccountKind.Microsoft,
                Skin = AccountSkin.MojangName,
                SkinPath = null,
                SkinModel = null,
            };

            _accounts[_accounts.IndexOf(byName)] = upgraded;
            return upgraded;
        }

        var account = new PlayerAccount(
            Guid.NewGuid().ToString("N"),
            name,
            normalizedUuid,
            AccountKind.Microsoft,
            DateTimeOffset.UtcNow,
            AccountSkin.MojangName);

        _accounts.Add(account);
        return account;
    }

    public bool Remove(string id)
    {
        var account = Find(id);
        return account is not null && _accounts.Remove(account);
    }

    /// <summary>
    /// 改用户名：离线 UUID 跟着名字重算（和 Java 的离线规则一致）。
    /// 正版 / 第三方账号以后走各自的重命名流程，这里只处理离线。
    /// </summary>
    public PlayerAccount? Rename(string id, string newUsername)
    {
        var name = newUsername.Trim();

        if (!PlayerAccount.IsValidUsername(name))
            throw new ArgumentException("用户名只能是 1~16 个字母、数字或下划线。", nameof(newUsername));

        var index = _accounts.FindIndex(account =>
            string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return null;

        if (_accounts.Any(account =>
                !string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(account.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"已经有名为「{name}」的账号了");
        }

        var current = _accounts[index];

        _accounts[index] = current.Kind == AccountKind.Microsoft
            ? current with { Username = name }
            : current with { Username = name, Uuid = PlayerAccount.OfflineUuid(name) };

        return _accounts[index];
    }

    /// <summary>换皮肤：只动皮肤相关字段，用户名 / UUID / Id 都不变。</summary>
    public PlayerAccount? SetSkin(string id, AccountSkin skin, string? skinPath, string? skinModel)
    {
        var index = _accounts.FindIndex(account =>
            string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return null;

        _accounts[index] = _accounts[index] with
        {
            Skin = skin,
            SkinPath = skinPath,
            SkinModel = skinModel,
        };

        return _accounts[index];
    }

    /// <summary>返回问题描述；空列表表示数据健康。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in _accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id) || !seenIds.Add(account.Id))
                problems.Add($"账号 Id 缺失或重复：'{account.Id}'");

            if (!PlayerAccount.IsValidUsername(account.Username))
                problems.Add($"账号用户名非法：'{account.Username}'");
            else if (!seenNames.Add(account.Username))
                problems.Add($"账号用户名重复：'{account.Username}'");

            if (account.Uuid.Length != 32)
                problems.Add($"账号 UUID 非法：'{account.Uuid}'");
        }

        return problems;
    }
}
