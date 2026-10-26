using NPMC.Launcher.Core.Accounts;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>账号页左侧列表的一行。</summary>
public sealed class AccountListItem : ObservableObject
{
    private bool _isDefault;

    public AccountListItem(PlayerAccount account) => Account = account;

    public PlayerAccount Account { get; private set; }

    /// <summary>就地更新（改名 / 换皮肤），不重建列表。</summary>
    public void ApplyAccount(PlayerAccount account)
    {
        Account = account;

        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(UuidDisplay));
        OnPropertyChanged(nameof(UuidShort));
        OnPropertyChanged(nameof(Initial));
    }

    public string Id => Account.Id;
    public string Username => Account.Username;
    public string KindText => Account.KindText;
    public string UuidDisplay => Account.UuidDisplay;

    public string CreatedText => Account.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>列表里只显示前 8 位，完整 UUID 在右侧详情里。</summary>
    public string UuidShort => Account.Uuid.Length >= 8 ? Account.Uuid[..8] : Account.Uuid;

    public string Initial => Account.Username.Length > 0
        ? Account.Username[..1].ToUpperInvariant()
        : "?";

    public bool IsDefault
    {
        get => _isDefault;
        private set
        {
            if (!SetProperty(ref _isDefault, value)) return;
            OnPropertyChanged(nameof(DefaultText));
        }
    }

    public string DefaultText => IsDefault ? "默认" : "";

    public void SetDefault(bool isDefault) => IsDefault = isDefault;
}

/// <summary>账号下拉里的一项：<see cref="Id"/> 为 null 表示"跟随默认账号"。</summary>
public sealed class AccountChoiceItem : ObservableObject
{
    private string _display = "";

    public string? Id { get; init; }

    public string Display
    {
        get => _display;
        set => SetProperty(ref _display, value);
    }

    public override string ToString() => Display;
}
