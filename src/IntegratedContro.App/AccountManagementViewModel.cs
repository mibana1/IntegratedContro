using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class AccountManagementViewModel : FeatureViewModel
{
    private readonly IAccountManagementHost _host;
    public ObservableCollection<AccountView> Accounts { get; } = [];
    public IEnumerable<AccountRole> AccountRoles => Enum.GetValues<AccountRole>();
    public Func<string> ReadNewPassword { get; set; } = () => "";
    public Action ClearNewPassword { get; set; } = () => { };
    public AsyncCommand CreateAccountCommand { get; }
    public AsyncCommand LoadAccountCommand { get; }
    public AsyncCommand UpdateAccountCommand { get; }
    public AccountManagementViewModel(IAccountManagementHost host) : base(host)
    {
        _host = host;
        CreateAccountCommand = Command(async () =>
        {
            var password = ReadNewPassword();
            try { await _host.CreateAccountAsync(new CreateAccountRequest(Generation, NewAccountName, password, NewAccountRole, AccountAllDevices, ParseScope())); }
            finally { ClearNewPassword(); }
            ReportStatus("앱 계정을 등록했습니다.");
        }, () => CanConfigure);
        LoadAccountCommand = Command(() =>
        {
            if (SelectedAccount is null) return Task.CompletedTask;
            AccountEnabled = SelectedAccount.Enabled; AccountAllDevices = SelectedAccount.AllDevices;
            AccountDeviceIds = string.Join(",", SelectedAccount.DeviceIds); NotifyEditors(); return Task.CompletedTask;
        }, () => SelectedAccount is not null);
        UpdateAccountCommand = Command(async () =>
        {
            if (SelectedAccount is null) throw new ArgumentException("권한을 변경할 계정을 선택하세요.");
            await _host.UpdateAccountAsync(new UpdateAccountRequest(Generation, SelectedAccount.Id, AccountEnabled, AccountAllDevices, ParseScope()));
            ReportStatus("계정 권한을 반영했습니다. 접수 작업도 다음 전송 전에 현재 권한을 재검증합니다.");
        }, () => CanConfigure);
    }
    public string NewAccountName { get; set; } = "";
    public AccountRole NewAccountRole { get; set; } = AccountRole.Operator;
    public bool AccountAllDevices { get; set; } = true;
    public string AccountDeviceIds { get; set; } = "";
    private AccountView? _selectedAccount;
    public AccountView? SelectedAccount { get => _selectedAccount; set { Set(ref _selectedAccount, value); RefreshCommands(); } }
    public bool AccountEnabled { get; set; } = true;
    private Guid[] ParseScope() => AccountDeviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToArray();
    protected override void OnContextChanged(FeatureContext previous)
    {
        if (previous.State?.Session.Id != State?.Session.Id || Context.Closing && !previous.Closing)
        {
            SelectedAccount = null; NewAccountName = ""; NewAccountRole = AccountRole.Operator;
            AccountAllDevices = true; AccountDeviceIds = ""; AccountEnabled = true; ClearNewPassword();
            NotifyEditors(); Changed(nameof(NewAccountName)); Changed(nameof(NewAccountRole));
        }
        if (!ReferenceEquals(previous.State, State))
        {
            var accountId = SelectedAccount?.Id;
            Replace(Accounts, State?.Accounts ?? []);
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == accountId);
        }
    }
    private void NotifyEditors()
    {
        Changed(nameof(AccountAllDevices)); Changed(nameof(AccountDeviceIds)); Changed(nameof(AccountEnabled));
    }
}
