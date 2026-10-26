using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Infrastructure.Skins;
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

    private string _accountSearchText = "";
    private bool _isCreatingAccount;
    private int _newAccountVerificationIndex;
    private CancellationTokenSource? _autoCreateCts;

    private bool _isDeletingAccount;
    private string _deleteAccountText = "删除账号";

    private AccountChoiceItem? _selectedAccountChoice;
    private bool _suppressAccountSelection;
    private bool _suppressAccountDraftCancel;

    public ICommand ShowAccountsCommand { get; private set; }
    public ICommand CreateAccountCommand { get; private set; }
    public ICommand CancelCreateAccountCommand { get; private set; }
    public ICommand SaveAccountNameCommand { get; private set; }
    public ICommand DeleteAccountCommand { get; private set; }
    public ICommand SetDefaultAccountCommand { get; private set; }
    public ICommand CopyAccountUuidCommand { get; private set; }
    public ICommand FetchMojangSkinCommand { get; private set; }

    private bool _suppressSkinUi;
    private int _selectedSkinIndex;
    private int _selectedSkinArmIndex;
    private Bitmap? _skinPreview;
    private string _skinHint = "";
    private bool _isFetchingSkin;

    public ObservableCollection<string> SkinOptions { get; } = new()
    {
        "Steve", "Alex", "自定义皮肤", "使用同名正版皮肤",
    };

    public ObservableCollection<string> SkinArmOptions { get; } = new()
    {
        "经典（手臂 4px）", "纤细（手臂 3px）",
    };

    private void InitializeAccountCommands()
    {
        ShowAccountsCommand = new RelayCommand(() => Navigate(NavPage.Accounts));
        CreateAccountCommand = new RelayCommand(BeginCreateAccount);
        CancelCreateAccountCommand = new RelayCommand(CancelCreateAccount);
        SaveAccountNameCommand = new AsyncRelayCommand(SaveAccountNameAsync);
        DeleteAccountCommand = new AsyncRelayCommand(DeleteAccountAsync,
            () => SelectedAccount is not null);
        SetDefaultAccountCommand = new RelayCommand(SetDefaultAccount,
            () => SelectedAccount is not null);
        CopyAccountUuidCommand = new RelayCommand(CopyAccountUuid,
            () => SelectedAccount is not null && CopyToClipboard is not null);
        FetchMojangSkinCommand = new AsyncRelayCommand(FetchMojangSkinAsync,
            () => SelectedAccount is not null && !IsFetchingSkin);
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

    public string AccountListEmptyText => _accountCatalog.Count == 0
        ? "还没有账号，点上面的「创建账号」新建一个"
        : "没有匹配的账号";

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

            // XAML 在重建下拉时会推回旧实例或 null；只接受当前集合里的项
            if (value is not null && !DefaultAccountOptions.Contains(value)) return;
            if (value is null && _settingsDefaultAccountId is not null) return;

            if (!SetProperty(ref _settingsDefaultAccountChoice, value)) return;

            var newId = value?.Id;
            if (string.Equals(newId, _settingsDefaultAccountId, StringComparison.Ordinal)) return;

            _settingsDefaultAccountId = newId;
            SaveSettings();

            RefreshAccountDefaults();
            RefreshDefaultOptionLabels();
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
        set
        {
            if (!SetProperty(ref _newAccountName, value)) return;

            // 新建档案：名字一填好就自动落盘（见 ScheduleAutoCreate）
            if (IsCreatingAccount) ScheduleAutoCreate();
        }
    }

    /// <summary>左侧搜索框：按用户名或 UUID 过滤账号列表。</summary>
    public string AccountSearchText
    {
        get => _accountSearchText;
        set
        {
            if (!SetProperty(ref _accountSearchText, value)) return;
            RebuildAccountViews(keepSelection: true);
        }
    }

    /// <summary>右侧正在新建"空档案"：还没填名字，也没落盘。</summary>
    public bool IsCreatingAccount
    {
        get => _isCreatingAccount;
        private set
        {
            if (!SetProperty(ref _isCreatingAccount, value)) return;
            OnPropertyChanged(nameof(ShowAccountDetail));
            OnPropertyChanged(nameof(ShowAccountEmptyHint));
            OnPropertyChanged(nameof(AccountDetailTitle));
            OnPropertyChanged(nameof(AccountDetailSubtitle));
        }
    }

    /// <summary>验证方式：0 = 离线（默认），1 = 微软正版（暂未接入）。</summary>
    public int NewAccountVerificationIndex
    {
        get => _newAccountVerificationIndex;
        set
        {
            if (!SetProperty(ref _newAccountVerificationIndex, value)) return;

            if (value != 0)
            {
                AccountStatus = "微软正版验证还在等 Azure 审批，先按离线账号创建。";
                _newAccountVerificationIndex = 0;
                OnPropertyChanged(nameof(NewAccountVerificationIndex));
                return;
            }

            if (IsCreatingAccount) ScheduleAutoCreate();
        }
    }

    public string NewAccountHint => "填写用户名后自动保存；名称为空则取消，不会留下空档案。验证方式未选时按离线创建。";

    public bool ShowAccountDetail => HasSelectedAccount && !IsCreatingAccount;

    public bool ShowAccountEmptyHint => !ShowAccountDetail && !IsCreatingAccount;

    // ---- 选中的账号（右侧详情）----

    public AccountListItem? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value)) return;

            // 点列表里的已有账号 = 放弃"新建空档案"
            if (!_suppressAccountDraftCancel && IsCreatingAccount && value is not null)
                CancelCreateAccount();

            _isDeletingAccount = false;
            DeleteAccountText = "删除账号";
            AccountNameEdit = value?.Username ?? "";

            OnPropertyChanged(nameof(HasSelectedAccount));
            OnPropertyChanged(nameof(ShowAccountDetail));
            OnPropertyChanged(nameof(ShowAccountEmptyHint));
            OnPropertyChanged(nameof(AccountDetailTitle));
            OnPropertyChanged(nameof(AccountDetailSubtitle));
            OnPropertyChanged(nameof(SelectedAccountIsDefault));

            RaiseAccountCommandStates();
            RefreshSkinView();
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null;

    public string AccountDetailTitle => IsCreatingAccount
        ? "新建账号"
        : SelectedAccount?.Username ?? "账号";

    public string AccountDetailSubtitle => IsCreatingAccount
        ? "选择验证方式（目前只有离线），填写用户名后自动保存"
        : SelectedAccount is { } account
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
        var query = AccountSearchText.Trim();

        Accounts.Clear();

        foreach (var account in _accountCatalog.Accounts)
        {
            if (query.Length > 0
                && !account.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !account.Uuid.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Accounts.Add(new AccountListItem(account));
        }

        RefreshAccountDefaults();

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(AccountListEmptyText));

        _suppressAccountDraftCancel = true;

        try
        {
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedId)
                              ?? Accounts.FirstOrDefault();
        }
        finally
        {
            _suppressAccountDraftCancel = false;
        }

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

    /// <summary>原地更新"跟随默认账号（xxx）"的文案，不重建集合，避免 XAML 回写旧选中项。</summary>
    private void RefreshDefaultOptionLabels()
    {
        var defaultName = _accountCatalog.Find(_settingsDefaultAccountId)?.Username
                          ?? _accountCatalog.Accounts.FirstOrDefault()?.Username;

        if (AccountOptions.Count > 0)
        {
            AccountOptions[0].Display = defaultName is null
                ? "跟随默认账号（还没有账号）"
                : $"跟随默认账号（{defaultName}）";
        }

        if (DefaultAccountOptions.Count > 0) DefaultAccountOptions[0].Display = "自动（第一个账号）";
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
            // 只接受当前下拉集合里的项：XAML 重建列表时会推回已脱离集合的旧实例
            if (value is null || !AccountOptions.Contains(value)) return;

            if (!SetProperty(ref _selectedAccountChoice, value)) return;
            if (_suppressAccountSelection) return;

            _ = PersistInstanceAccountAsync(value.Id);
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

    /// <summary>「创建账号」：新建一个空档案，等用户填用户名；没填就取消，不会留下空账号。</summary>
    private void BeginCreateAccount()
    {
        _autoCreateCts?.Cancel();

        _newAccountName = "";
        OnPropertyChanged(nameof(NewAccountName));
        _newAccountVerificationIndex = 0;
        OnPropertyChanged(nameof(NewAccountVerificationIndex));

        IsCreatingAccount = true;
        AccountStatus = NewAccountHint;
    }

    /// <summary>取消新建：丢弃草稿（从未落盘）。</summary>
    private void CancelCreateAccount()
    {
        _autoCreateCts?.Cancel();
        _autoCreateCts = null;

        if (!IsCreatingAccount) return;

        IsCreatingAccount = false;

        _newAccountName = "";
        OnPropertyChanged(nameof(NewAccountName));

        AccountStatus = "已取消创建账号";
    }

    /// <summary>离开账号页时悄悄丢弃草稿（不弹提示）。</summary>
    private void CancelAccountDraft()
    {
        if (!IsCreatingAccount) return;

        _autoCreateCts?.Cancel();
        _autoCreateCts = null;
        IsCreatingAccount = false;

        _newAccountName = "";
        OnPropertyChanged(nameof(NewAccountName));
    }

    /// <summary>用户名填好后延迟 700ms 自动保存；期间继续输入会重新计时。</summary>
    private async void ScheduleAutoCreate()
    {
        _autoCreateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoCreateCts = cts;

        var name = NewAccountName.Trim();

        if (name.Length == 0)
        {
            AccountStatus = "请输入用户名；留空并取消则不会创建账号。";
            return;
        }

        if (!PlayerAccount.IsValidUsername(name))
        {
            AccountStatus = "用户名只能是 1~16 个字母、数字或下划线。";
            return;
        }

        // 验证方式没选/选了不可用的正版 → 按离线创建（正版通道等审批）
        if (NewAccountVerificationIndex != 0)
        {
            AccountStatus = "微软正版验证还在等 Azure 审批，先按离线账号创建。";
            return;
        }

        try
        {
            await Task.Delay(700, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || !IsCreatingAccount) return;

        await AutoCreateAccountAsync(name);
    }

    private async Task AutoCreateAccountAsync(string name)
    {
        try
        {
            var store = new JsonAccountStore(_launcher);
            var catalog = new AccountCatalog(await store.LoadAsync());
            var account = catalog.AddOffline(name);

            await store.SaveAsync(catalog.Accounts);

            // 先收起草稿再刷新列表，否则 Rebuild 的选中逻辑会把它当成"点列表取消创建"
            IsCreatingAccount = false;

            _newAccountName = "";
            OnPropertyChanged(nameof(NewAccountName));

            await ReloadAccountsAsync();

            _suppressAccountDraftCancel = true;

            try
            {
                SelectedAccount = Accounts.FirstOrDefault(item => item.Id == account.Id);
            }
            finally
            {
                _suppressAccountDraftCancel = false;
            }

            AccountStatus = $"已自动保存离线账号「{account.Username}」· UUID {account.UuidDisplay}";
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

            // 不重建下拉集合：重建会让 XAML 把旧 SelectedItem 推回来，
            // 结果徽标显示新账号、状态栏也写着新账号，实际默认值却被改回旧账号。
            RefreshDefaultOptionLabels();
            RefreshDefaultAccountSelection();
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
        (FetchMojangSkinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---- 皮肤 ----

    private string SkinCacheDirectory => Path.Combine(_launcher.Root, "cache", "skins");

    public int SelectedSkinIndex
    {
        get => _selectedSkinIndex;
        set
        {
            if (!SetProperty(ref _selectedSkinIndex, value)) return;

            NotifySkinFlags();

            if (_suppressSkinUi) return;

            OnSkinChoiceChanged();
        }
    }

    public int SelectedSkinArmIndex
    {
        get => _selectedSkinArmIndex;
        set
        {
            if (!SetProperty(ref _selectedSkinArmIndex, value)) return;
            if (_suppressSkinUi) return;

            var model = value == 1 ? "slim" : "classic";
            _ = SaveSkinAsync(SelectedSkinIndexToKind(_selectedSkinIndex),
                SelectedAccount?.Account.SkinPath, model);
        }
    }

    public bool IsCustomSkin => _selectedSkinIndex == 2;

    public bool IsMojangSkin => _selectedSkinIndex == 3;

    public Bitmap? SkinPreview
    {
        get => _skinPreview;
        private set
        {
            if (ReferenceEquals(_skinPreview, value)) return;

            _skinPreview?.Dispose();
            _skinPreview = value;

            OnPropertyChanged(nameof(SkinPreview));
            OnPropertyChanged(nameof(HasSkinPreview));
            OnPropertyChanged(nameof(SkinPreviewLabel));
        }
    }

    public bool HasSkinPreview => _skinPreview is not null;

    public string SkinPreviewLabel => HasSkinPreview
        ? ""
        : SelectedSkinIndexToKind(_selectedSkinIndex) switch
        {
            AccountSkin.Alex => "ALEX",
            AccountSkin.Steve => "STEVE",
            _ => "皮肤",
        };

    public string SkinHint
    {
        get => _skinHint;
        private set => SetProperty(ref _skinHint, value);
    }

    public bool IsFetchingSkin
    {
        get => _isFetchingSkin;
        private set
        {
            if (!SetProperty(ref _isFetchingSkin, value)) return;
            (FetchMojangSkinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void NotifySkinFlags()
    {
        OnPropertyChanged(nameof(IsCustomSkin));
        OnPropertyChanged(nameof(IsMojangSkin));
        OnPropertyChanged(nameof(SkinPreviewLabel));
    }

    private static AccountSkin SelectedSkinIndexToKind(int index) => index switch
    {
        1 => AccountSkin.Alex,
        2 => AccountSkin.Custom,
        3 => AccountSkin.MojangName,
        _ => AccountSkin.Steve,
    };

    /// <summary>把选中账号的皮肤设置映射到右侧控件 + 预览图。</summary>
    private void RefreshSkinView()
    {
        _suppressSkinUi = true;

        try
        {
            var account = SelectedAccount?.Account;

            _selectedSkinIndex = account is null
                ? 0
                : account.Skin switch
                {
                    AccountSkin.Alex => 1,
                    AccountSkin.Custom => 2,
                    AccountSkin.MojangName => 3,
                    _ => 0,
                };

            _selectedSkinArmIndex = string.Equals(account?.SkinModel, "slim", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

            OnPropertyChanged(nameof(SelectedSkinIndex));
            OnPropertyChanged(nameof(SelectedSkinArmIndex));
            NotifySkinFlags();

            LoadSkinPreview(account?.SkinPath);

            SkinHint = account switch
            {
                null => "",
                { Skin: AccountSkin.Custom } => "选一个 64×64 的 PNG；手臂粗细按皮肤样式选。",
                { Skin: AccountSkin.MojangName } => "按同名正版账号从 Mojang 取皮肤；找不到时会提示。",
                _ => "内置 Steve / Alex 只做预览；进游戏显示需要皮肤站或模组支持。",
            };
        }
        finally
        {
            _suppressSkinUi = false;
        }
    }

    private void LoadSkinPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SkinPreview = null;
            return;
        }

        try
        {
            SkinPreview = new Bitmap(path);
        }
        catch
        {
            SkinPreview = null;
        }
    }

    private void OnSkinChoiceChanged()
    {
        var skin = SelectedSkinIndexToKind(_selectedSkinIndex);

        if (skin == AccountSkin.Steve || skin == AccountSkin.Alex)
        {
            _ = SaveSkinAsync(skin, null, null);
            return;
        }

        var account = SelectedAccount?.Account;

        _ = SaveSkinAsync(skin, account?.SkinPath, account?.SkinModel);

        if (skin == AccountSkin.Custom && string.IsNullOrWhiteSpace(account?.SkinPath))
            SkinHint = "还没选皮肤文件，点「选择 PNG」。";
    }

    /// <summary>选好自定义皮肤后由窗口调用（文件对话框在代码里）。</summary>
    public async void SetAccountSkinFile(string path)
    {
        if (SelectedAccount is not { } selected) return;

        if (!File.Exists(path))
        {
            SkinHint = "文件不存在";
            return;
        }

        try
        {
            Directory.CreateDirectory(SkinCacheDirectory);

            var destination = Path.Combine(
                SkinCacheDirectory, $"{selected.Id}-{DateTime.UtcNow.Ticks}.png");

            File.Copy(path, destination, overwrite: true);

            await SaveSkinAsync(AccountSkin.Custom, destination,
                _selectedSkinArmIndex == 1 ? "slim" : "classic");
        }
        catch (Exception ex)
        {
            SkinHint = $"读取皮肤失败：{ex.GetBaseException().Message}";
        }
    }

    private async Task FetchMojangSkinAsync()
    {
        if (SelectedAccount is not { } selected) return;

        if (IsFetchingSkin) return;

        IsFetchingSkin = true;
        SkinHint = "正在从 Mojang 查同名正版皮肤…";

        try
        {
            var fetched = await MojangSkinClient.TryFetchAsync(selected.Username, SkinCacheDirectory);

            if (fetched is null)
            {
                SkinHint = "找不到同名正版账号（或网络不可用），已保留当前皮肤设置。";
                return;
            }

            await SaveSkinAsync(AccountSkin.MojangName, fetched.Path, fetched.Model);
            SkinHint = $"已取回正版皮肤（{fetched.Model}）。";
        }
        catch (Exception ex)
        {
            SkinHint = $"获取皮肤失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            IsFetchingSkin = false;
        }
    }

    private async Task SaveSkinAsync(AccountSkin skin, string? path, string? model)
    {
        if (SelectedAccount is not { } selected) return;

        try
        {
            var store = new JsonAccountStore(_launcher);
            var catalog = new AccountCatalog(await store.LoadAsync());

            var updated = catalog.SetSkin(selected.Id, skin, path, model);
            if (updated is null) return;

            await store.SaveAsync(catalog.Accounts);

            selected.ApplyAccount(updated);
            RefreshSkinView();

            AccountStatus = $"账号「{updated.Username}」的皮肤：{updated.SkinText}";
        }
        catch (Exception ex)
        {
            SkinHint = $"保存皮肤失败：{ex.GetBaseException().Message}";
        }
    }

    private void NotifyAccountSettingsChanged()
    {
        OnPropertyChanged(nameof(SelectedAccountIsDefault));
    }
}
