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

    public PlayerAccount? FindByUsername(string? username, AccountKind? kind = null) => username is null
        ? null
        : _accounts.FirstOrDefault(account =>
            (kind is null || account.Kind == kind)
            && string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));

    public PlayerAccount AddOffline(string username)
    {
        var name = username.Trim();

        if (!PlayerAccount.IsValidUsername(name))
            throw new ArgumentException("用户名只能是 1~16 个字母、数字或下划线。", nameof(username));

        if (FindByUsername(name, AccountKind.Offline) is not null)
            throw new InvalidOperationException($"已经有名为「{name}」的离线账号了");

        var account = PlayerAccount.CreateOffline(name);
        _accounts.Add(account);
        return account;
    }

    /// <summary>
    /// 登录成功后创建/更新微软账号。
    /// 同名**离线**账号不会被覆盖：保留它，另外新增一个微软账号。
    /// 已有同 UUID 或同名微软账号时只更新那一个。
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

        // 只合并同名微软账号；同名离线账号保留，另建一个微软账号。
        var byName = FindByUsername(name, AccountKind.Microsoft);

        if (byName is not null)
        {
            var updated = byName with
            {
                Username = name,
                Uuid = normalizedUuid,
                Skin = AccountSkin.MojangName,
                SkinPath = null,
                SkinModel = null,
            };

            _accounts[_accounts.IndexOf(byName)] = updated;
            return updated;
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

        var current = _accounts[index];

        if (_accounts.Any(account =>
                !string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase)
                && account.Kind == current.Kind
                && string.Equals(account.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"已经有名为「{name}」的{current.KindText}账号了");
        }

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
            else if (!seenNames.Add(account.Username + "|" + account.Kind))
                problems.Add($"同类型账号用户名重复：'{account.Username}'（{account.KindText}）");

            if (account.Uuid.Length != 32)
                problems.Add($"账号 UUID 非法：'{account.Uuid}'");
        }

        return problems;
    }
}
