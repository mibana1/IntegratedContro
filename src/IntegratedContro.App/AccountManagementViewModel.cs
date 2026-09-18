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
    private AccountRole _newAccountRole = AccountRole.Operator;
    public AccountRole NewAccountRole { get => _newAccountRole; set { Set(ref _newAccountRole, value); Changed(nameof(HiperwallScopeSummary)); } }
    private bool _accountAllDevices = true;
    public bool AccountAllDevices { get => _accountAllDevices; set { if (Set(ref _accountAllDevices, value)) { Changed(nameof(SelectedDevicesOnly)); Changed(nameof(HiperwallScopeSummary)); } } }
    public bool SelectedDevicesOnly { get => !AccountAllDevices; set => AccountAllDevices = !value; }
    private string _accountDeviceIds = "";
    public string AccountDeviceIds { get => _accountDeviceIds; set { _accountDeviceIds = value; RefreshDeviceChoices(); } }
    private AccountView? _selectedAccount;
    public AccountView? SelectedAccount { get => _selectedAccount; set { Set(ref _selectedAccount, value); Changed(nameof(SavedScopeSummary)); Changed(nameof(HiperwallScopeSummary)); RefreshCommands(); } }
    private bool _accountEnabled = true;
    public bool AccountEnabled { get => _accountEnabled; set { Set(ref _accountEnabled, value); Changed(nameof(HiperwallScopeSummary)); } }
    public ObservableCollection<AccountDeviceChoice> DeviceChoices { get; } = [];
    public string HiperwallScopeSummary => $"반영할 Hiperwall 권한: {(AccountEnabled && (SelectedAccount?.Role ?? NewAccountRole) != AccountRole.Viewer && AccountAllDevices ? "제어 허용 조건 충족" : "제어 불가")} · 활성 관리자/운영자 + 전체 장비 범위가 필요합니다. 연결·사용권·Hiperwall 쓰기 상태도 확인합니다.";
    public string SavedScopeSummary => SelectedAccount is not { } a ? "계정 목록에서 저장된 권한을 확인할 계정을 선택하세요." :
        $"현재 저장된 범위: {(a.AllDevices ? "전체 장비 (앞으로 추가될 장비 포함)" : $"선택한 장비 {a.DeviceIds.Length}대")} · Hiperwall {(a.Enabled && a.Role != AccountRole.Viewer && a.AllDevices ? "제어 허용 조건 충족" : "제어 불가")}";
    private bool _refreshingChoices;
    private void RefreshDeviceChoices()
    {
        if (_refreshingChoices) return;
        _refreshingChoices = true;
        var selected = AccountDeviceIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        foreach (var d in State?.Devices ?? [])
        {
            var connection = State!.DeviceConnections.FirstOrDefault(c => c.Id == d.ConnectionId);
            var label = $"{d.Name} · {(d.Location.Length == 0 ? "위치 미지정" : d.Location)} · {connection?.Describe(State.Pcs, State.Models) ?? d.Connection.Endpoint}" + (d.Connection.Address.Length > 0 ? $" · 장비 주소: {d.Connection.Address}" : "");
            var item = DeviceChoices.FirstOrDefault(c => c.Id == d.Id);
            if (item is null) { item = new(d.Id, label, OnChoiceChanged); DeviceChoices.Add(item); }
            item.Label = label; item.IsSelected = selected.Contains(d.Id);
        }
        foreach (var item in DeviceChoices.Where(c => State?.Devices.All(d => d.Id != c.Id) != false).ToArray())
        {
            if (selected.Contains(item.Id)) { item.Label = "목록에 없는 기존 권한 장비 · 진단에서 확인"; item.IsSelected = true; }
            else DeviceChoices.Remove(item);
        }
        foreach (var id in selected.Where(id => DeviceChoices.All(c => c.Id != id)))
            DeviceChoices.Add(new(id, "목록에 없는 기존 권한 장비 · 진단에서 확인", OnChoiceChanged) { IsSelected = true });
        _refreshingChoices = false;
    }
    private void OnChoiceChanged()
    {
        if (_refreshingChoices) return;
        _accountDeviceIds = string.Join(",", DeviceChoices.Where(c => c.IsSelected).Select(c => c.Id));
    }
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
            RefreshDeviceChoices();
        }
    }
    private void NotifyEditors()
    {
        Changed(nameof(AccountAllDevices)); Changed(nameof(AccountDeviceIds)); Changed(nameof(AccountEnabled));
    }
}

public sealed class AccountDeviceChoice(Guid id, string label, Action changed) : Bindable
{
    public Guid Id { get; } = id;
    private string _label = label;
    public string Label { get => _label; set => Set(ref _label, value); }
    private bool _selected;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) changed(); } }
}
