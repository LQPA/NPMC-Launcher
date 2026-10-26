using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 账号页（左侧导航的「账号」）与"每个实例用哪个账号"。
///
/// 目前只有离线账号：用户名 + 按 Minecraft 离线规则算出的 UUID，不联网、不涉及令牌。
/// 账号存在 &lt;root&gt;\accounts.json，实例只记 AccountId，删账号不会破坏实例数据
/// （找不到账号时自动退回默认账号）。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private AccountCatalog _accountCatalog = new();
    private bool _accountsLoading;

    private string _accountStatus = "";
    private string _newAccountName = "";
    private AccountListItem? _selectedAccount;
    private string _accountNameEdit = "";

    private bool _isDeletingAccount;
    private string _deleteAccountText = "删除账号";

    private AccountChoiceItem? _selectedAccountChoice;
    private bool _suppressAccountSelection;

    public ICommand ShowAccountsCommand { get; private set; }
    public ICommand CreateAccountCommand { get; private set; }
    public ICommand SaveAccountNameCommand { get; private set; }
    public ICommand DeleteAccountCommand { get; private set; }
    public ICommand SetDefaultAccountCommand { get; private set; }
    public ICommand CopyAccountUuidCommand { get; private set; }

    private void InitializeAccountCommands()
    {
        ShowAccountsCommand = new RelayCommand(() => Navigate(NavPage.Accounts));
        CreateAccountCommand = new AsyncRelayCommand(CreateAccountAsync);
        SaveAccountNameCommand = new AsyncRelayCommand(SaveAccountNameAsync);
        DeleteAccountCommand = new AsyncRelayCommand(DeleteAccountAsync,
            () => SelectedAccount is not null);
        SetDefaultAccountCommand = new RelayCommand(SetDefaultAccount,
            () => SelectedAccount is not null);
        CopyAccountUuidCommand = new RelayCommand(CopyAccountUuid,
            () => SelectedAccount is not null && CopyToClipboard is not null);
    }

    // ---- 导航与页面状态 ----

    public bool IsAccountsPage => _navPage == NavPage.Accounts;

    public string AccountsNavBackground => IsAccountsPage ? "#2B3A55" : "Transparent";

    public string AccountsNavForeground => IsAccountsPage ? "#DCE4F5" : "#949CA9";

    public string AccountsPath => new JsonAccountStore(_launcher).FilePath;

    public string AccountStatus
    {
        get => _accountStatus;
        private set
        {
            if (!SetProperty(ref _accountStatus, value)) return;
            OnPropertyChanged(nameof(HasAccountStatus));
        }
    }

    public bool HasAccountStatus => _accountStatus.Length > 0;

    public ObservableCollection<AccountListItem> Accounts { get; } = new();

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>实例概览里的账号下拉：第一项是"跟随默认"。</summary>
    public ObservableCollection<AccountChoiceItem> AccountOptions { get; } = new();

    /// <summary>设置页的"默认账号"下拉（没有"跟随默认"这一项）。</summary>
    public ObservableCollection<AccountChoiceItem> DefaultAccountOptions { get; } = new();

    private AccountChoiceItem? _settingsDefaultAccountChoice;

    /// <summary>设置页：全局默认账号。写进 settings.json 的 DefaultAccountId。</summary>
    public AccountChoiceItem? SettingsDefaultAccountChoice
    {
        get => _settingsDefaultAccountChoice;
        set
        {
            // 账号列表还没载入时，XAML 会把 SelectedItem 推成 null；
            // 这时不能当成"用户清空了默认账号"，否则会覆盖 settings.json。
            if (!_accountOptionsReady) return;

            if (!SetProperty(ref _settingsDefaultAccountChoice, value)) return;

            var newId = value?.Id;
            if (string.Equals(newId, _settingsDefaultAccountId, StringComparison.Ordinal)) return;

            _settingsDefaultAccountId = newId;
            SaveSettings();

            RefreshAccountDefaults();
            RebuildAccountOptions();
            NotifyAccountSettingsChanged();

            AccountStatus = newId is null
                ? "默认账号已改为：自动（第一个账号）"
                : $"默认账号已设为「{value?.Display}」";
        }
    }

    private bool _accountOptionsReady;

    private string _launchAccountName = "";

    /// <summary>最近一次启动实际使用的账号名（诊断与状态栏用）。</summary>
    public string LaunchAccountName
    {
        get => _launchAccountName;
        private set => SetProperty(ref _launchAccountName, value);
    }

    // ---- 新建 ----

    public string NewAccountName
    {
        get => _newAccountName;
        set => SetProperty(ref _newAccountName, value);
    }

    // ---- 选中的账号（右侧详情）----

    public AccountListItem? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value)) return;

            _isDeletingAccount = false;
            DeleteAccountText = "删除账号";
            AccountNameEdit = value?.Username ?? "";

            OnPropertyChanged(nameof(HasSelectedAccount));
            OnPropertyChanged(nameof(AccountDetailTitle));
            OnPropertyChanged(nameof(AccountDetailSubtitle));
            OnPropertyChanged(nameof(SelectedAccountIsDefault));

            RaiseAccountCommandStates();
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null;

    public string AccountDetailTitle => SelectedAccount?.Username ?? "账号";

    public string AccountDetailSubtitle => SelectedAccount is { } account
        ? $"{account.KindText}账号 · UUID {account.UuidDisplay}"
        : "在左侧选择一个账号";

    public bool SelectedAccountIsDefault => SelectedAccount?.IsDefault ?? false;

    /// <summary>右侧用户名编辑框（保存后才写回，UUID 会按新名字重算）。</summary>
    public string AccountNameEdit
    {
        get => _accountNameEdit;
        set
        {
            if (!SetProperty(ref _accountNameEdit, value)) return;
            RaiseAccountCommandStates();
        }
    }

    public bool IsDeletingAccount
    {
        get => _isDeletingAccount;
        private set => SetProperty(ref _isDeletingAccount, value);
    }

    public string DeleteAccountText
    {
        get => _deleteAccountText;
        private set => SetProperty(ref _deleteAccountText, value);
    }

    // ---- 加载 ----

    public async Task EnsureAccountsLoadedAsync()
    {
        if (_accountsLoading) return;
        _accountsLoading = true;

        try
        {
            _accountCatalog = new AccountCatalog(await new JsonAccountStore(_launcher).LoadAsync());
            RebuildAccountViews(keepSelection: true);
        }
        catch (Exception ex)
        {
            AccountStatus = $"读取账号失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            _accountsLoading = false;
        }
    }

    private async Task ReloadAccountsAsync()
    {
        _accountCatalog = new AccountCatalog(await new JsonAccountStore(_launcher).LoadAsync());
        RebuildAccountViews(keepSelection: true);
    }

    /// <summary>账号列表 / 下拉 / 默认标记一起重建。</summary>
    private void RebuildAccountViews(bool keepSelection)
    {
        var selectedId = keepSelection ? SelectedAccount?.Id : null;

        Accounts.Clear();
        foreach (var account in _accountCatalog.Accounts)
            Accounts.Add(new AccountListItem(account));

        RefreshAccountDefaults();

        OnPropertyChanged(nameof(HasAccounts));

        SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedId)
                          ?? Accounts.FirstOrDefault();

        RebuildAccountOptions();
        RebuildDefaultAccountOptions();

        if (SelectedInstance is { } instance) SyncAccountChoice(instance.Model);
    }

    private void RebuildDefaultAccountOptions()
    {
        var previousId = _settingsDefaultAccountChoice?.Id;
        var wasSuppressed = _suppressAccountSelection;
        _suppressAccountSelection = true;

        try
        {
            DefaultAccountOptions.Clear();
            DefaultAccountOptions.Add(new AccountChoiceItem
            {
                Id = null,
                Display = "自动（第一个账号）",
            });

            foreach (var account in _accountCatalog.Accounts)
            {
                DefaultAccountOptions.Add(new AccountChoiceItem
                {
                    Id = account.Id,
                    Display = $"{account.Username}（{account.KindText}）",
                });
            }

            _settingsDefaultAccountChoice =
                DefaultAccountOptions.FirstOrDefault(option => option.Id == (previousId ?? _settingsDefaultAccountId))
                ?? DefaultAccountOptions[0];

            OnPropertyChanged(nameof(SettingsDefaultAccountChoice));
            _accountOptionsReady = true;
        }
        finally
        {
            _suppressAccountSelection = wasSuppressed;
        }
    }

    /// <summary>settings.json 读进来之后，把下拉同步到文件里的 DefaultAccountId。</summary>
    private void RefreshDefaultAccountSelection()
    {
        if (DefaultAccountOptions.Count == 0) return;

        var wasSuppressed = _suppressAccountSelection;
        _suppressAccountSelection = true;

        try
        {
            _settingsDefaultAccountChoice =
                DefaultAccountOptions.FirstOrDefault(option => option.Id == _settingsDefaultAccountId)
                ?? DefaultAccountOptions[0];

            OnPropertyChanged(nameof(SettingsDefaultAccountChoice));
        }
        finally
        {
            _suppressAccountSelection = wasSuppressed;
        }
    }

    private void RefreshAccountDefaults()
    {
        var defaultId = _settingsDefaultAccountId;

        var effectiveDefaultId = _accountCatalog.Find(defaultId)?.Id
                                 ?? _accountCatalog.Accounts.FirstOrDefault()?.Id;

        foreach (var item in Accounts)
            item.SetDefault(item.Id == effectiveDefaultId);
    }

    private void RebuildAccountOptions()
    {
        var previousId = _selectedAccountChoice?.Id;
        var wasSuppressed = _suppressAccountSelection;
        _suppressAccountSelection = true;

        try
        {
            AccountOptions.Clear();

            var defaultName = _accountCatalog.Find(_settingsDefaultAccountId)?.Username
                              ?? _accountCatalog.Accounts.FirstOrDefault()?.Username;

            AccountOptions.Add(new AccountChoiceItem
            {
                Id = null,
                Display = defaultName is null ? "跟随默认账号（还没有账号）" : $"跟随默认账号（{defaultName}）",
            });

            foreach (var account in _accountCatalog.Accounts)
            {
                AccountOptions.Add(new AccountChoiceItem
                {
                    Id = account.Id,
                    Display = $"{account.Username}（{account.KindText}）",
                });
            }

            _selectedAccountChoice = AccountOptions.FirstOrDefault(option => option.Id == previousId)
                                     ?? AccountOptions[0];

            OnPropertyChanged(nameof(SelectedAccountChoice));
        }
        finally
        {
            _suppressAccountSelection = wasSuppressed;
        }
    }

    /// <summary>实例切换时把下拉同步到该实例自己的选择。</summary>
    private void SyncAccountChoice(LauncherInstance instance)
    {
        if (AccountOptions.Count == 0) return;

        var desiredId = instance.Settings.AccountId;
        var option = desiredId is null
            ? AccountOptions[0]
            : AccountOptions.FirstOrDefault(item => item.Id == desiredId) ?? AccountOptions[0];

        var wasSuppressed = _suppressAccountSelection;
        _suppressAccountSelection = true;

        try
        {
            _selectedAccountChoice = option;
            OnPropertyChanged(nameof(SelectedAccountChoice));
        }
        finally
        {
            _suppressAccountSelection = wasSuppressed;
        }
    }

    // ---- 每个实例用哪个账号 ----

    public AccountChoiceItem? SelectedAccountChoice
    {
        get => _selectedAccountChoice;
        set
        {
            if (!SetProperty(ref _selectedAccountChoice, value)) return;
            if (_suppressAccountSelection) return;

            _ = PersistInstanceAccountAsync(value?.Id);
        }
    }

    private async Task PersistInstanceAccountAsync(string? accountId)
    {
        if (SelectedInstance is not { } instance) return;

        try
        {
            var store = new JsonInstanceStore(_launcher);
            var catalog = new InstanceCatalog(await store.LoadAsync());

            var updated = catalog.UpdateSettings(
                instance.Id, instance.Model.Settings with { AccountId = accountId });

            if (updated is null) return;

            await store.SaveAsync(catalog.Instances);
            instance.ApplySettings(updated.Settings);

            var name = accountId is null
                ? "跟随默认账号"
                : _accountCatalog.Find(accountId)?.Username ?? "未知账号";

            Status = $"实例「{instance.Name}」使用的账号：{name}";
        }
        catch (Exception ex)
        {
            Status = $"保存实例账号失败：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>启动时解析账号：实例指定 → 全局默认 → 列表第一个；都没有则返回 null。</summary>
    public async Task<PlayerAccount?> ResolveLaunchAccountAsync(LauncherInstance instance)
    {
        await EnsureAccountsLoadedAsync();

        var settings = await LoadLauncherSettingsAsync();
        var accountId = instance.Settings.AccountId ?? settings.DefaultAccountId;

        return _accountCatalog.Find(accountId) ?? _accountCatalog.Accounts.FirstOrDefault();
    }

    // ---- 新建 / 改名 / 删除 / 默认 ----

    private async Task CreateAccountAsync()
    {
        try
        {
            var store = new JsonAccountStore(_launcher);
            var catalog = new AccountCatalog(await store.LoadAsync());

            var account = catalog.AddOffline(NewAccountName);
            await store.SaveAsync(catalog.Accounts);

            NewAccountName = "";
            await ReloadAccountsAsync();

            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == account.Id);
            AccountStatus = $"已创建离线账号「{account.Username}」· UUID {account.UuidDisplay}";
        }
        catch (Exception ex)
        {
            AccountStatus = $"创建失败：{ex.GetBaseException().Message}";
        }
    }

    private async Task SaveAccountNameAsync()
    {
        if (SelectedAccount is not { } selected) return;

        var name = AccountNameEdit.Trim();

        if (string.Equals(name, selected.Username, StringComparison.Ordinal))
        {
            AccountStatus = "用户名没有变化";
            return;
        }

        try
        {
            var store = new JsonAccountStore(_launcher);
            var catalog = new AccountCatalog(await store.LoadAsync());

            var updated = catalog.Rename(selected.Id, name)
                          ?? throw new InvalidOperationException("账号已不存在");

            await store.SaveAsync(catalog.Accounts);
            await ReloadAccountsAsync();

            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == updated.Id);
            AccountStatus = $"已改名为「{updated.Username}」，离线 UUID 重算为 {updated.UuidDisplay}";
        }
        catch (Exception ex)
        {
            AccountStatus = $"改名失败：{ex.GetBaseException().Message}";
        }
    }

    private async Task DeleteAccountAsync()
    {
        if (SelectedAccount is not { } selected) return;

        if (!IsDeletingAccount)
        {
            IsDeletingAccount = true;
            DeleteAccountText = "再次点击确认删除";
            AccountStatus = $"将删除账号「{selected.Username}」。引用它的实例会退回默认账号。再点一次确认。";

            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
            {
                IsDeletingAccount = false;
                DeleteAccountText = "删除账号";
            }, TaskScheduler.FromCurrentSynchronizationContext());

            return;
        }

        try
        {
            var store = new JsonAccountStore(_launcher);
            var catalog = new AccountCatalog(await store.LoadAsync());

            if (!catalog.Remove(selected.Id)) return;

            await store.SaveAsync(catalog.Accounts);

            // 删掉的正好是全局默认账号：清掉设置里的引用，避免默认账号指向不存在的 Id
            if (string.Equals(_settingsDefaultAccountId, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                var settings = await new JsonSettingsStore(_launcher).LoadAsync();
                await new JsonSettingsStore(_launcher).SaveAsync(settings with { DefaultAccountId = null });
                _settingsDefaultAccountId = null;
            }

            IsDeletingAccount = false;
            DeleteAccountText = "删除账号";

            await ReloadAccountsAsync();
            AccountStatus = $"已删除账号「{selected.Username}」";
        }
        catch (Exception ex)
        {
            AccountStatus = $"删除失败：{ex.GetBaseException().Message}";
        }
    }

    private void SetDefaultAccount()
    {
        if (SelectedAccount is not { } selected) return;

        _ = SetDefaultAccountAsync(selected.Id);
    }

    private async Task SetDefaultAccountAsync(string accountId)
    {
        try
        {
            var settings = await new JsonSettingsStore(_launcher).LoadAsync();
            await new JsonSettingsStore(_launcher).SaveAsync(settings with { DefaultAccountId = accountId });

            _settingsDefaultAccountId = accountId;
            RefreshAccountDefaults();
            RebuildAccountOptions();
            RebuildDefaultAccountOptions();
            NotifyAccountSettingsChanged();

            var account = _accountCatalog.Find(accountId);
            AccountStatus = $"默认账号已设为「{account?.Username ?? accountId}」";
        }
        catch (Exception ex)
        {
            AccountStatus = $"设置默认账号失败：{ex.GetBaseException().Message}";
        }
    }

    private void CopyAccountUuid()
    {
        if (SelectedAccount is not { } selected || CopyToClipboard is null) return;

        _ = CopyToClipboard(selected.Account.Uuid);
        AccountStatus = $"已复制 UUID：{selected.UuidDisplay}";
    }

    private void RaiseAccountCommandStates()
    {
        (DeleteAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveAccountNameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SetDefaultAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyAccountUuidCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void NotifyAccountSettingsChanged()
    {
        OnPropertyChanged(nameof(SelectedAccountIsDefault));
    }
}
