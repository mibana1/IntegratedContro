using System.Collections.ObjectModel;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class DeviceSettingsViewModel : FeatureViewModel
{
    private DeviceModel? _selectedModel;
    public DeviceModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            var previous = _selectedModel?.Id;
            _selectedModel = value;
            if (value is not null && previous != value.Id && !DeviceTransports.Contains(DeviceTransportId))
                DeviceTransportId = DeviceTransports.FirstOrDefault() ?? "";
            Changed(nameof(SelectedModel)); Changed(nameof(DeviceDriverId));
            Changed(nameof(DeviceTransports)); Changed(nameof(DeviceIsSimulation));
            if (previous != value?.Id) Changed(nameof(DeviceTransportId));
        }
    }
    public string DeviceModeSummary => State is null || State.Devices.Length == 0 ? "등록된 장비 없음" :
        State.Devices.All(d => State.Models.Any(m => m.Id == d.ModelId && m.IsSimulation)) ? "장비: 가상" :
        State.Devices.All(d => State.Models.Any(m => m.Id == d.ModelId && !m.IsSimulation)) ? "장비: 실장비 드라이버" : "장비: 혼합 / 드라이버 설정 확인";
    public string DeviceDriverId => SelectedModel?.DriverId ?? "";
    public IEnumerable<string> DeviceTransports => SelectedModel?.TransportIds ?? [];
    public bool DeviceIsSimulation => SelectedModel?.IsSimulation == true;
    private string _deviceTransportId = "virtual";
    public string DeviceTransportId
    {
        get => _deviceTransportId;
        set
        {
            // ComboBox clears SelectedItem while replacing its catalog. Keep the draft selection.
            if (!string.IsNullOrWhiteSpace(value)) Set(ref _deviceTransportId, value);
        }
    }
    public string DeviceEndpoint { get; set; } = "";
    public string DeviceAddress { get; set; } = "";
    public string DeviceTransportOptions { get; set; } = "{}";
    public string DeviceDriverOptions { get; set; } = "{}";

    private static Dictionary<string, string> ParseDeviceOptions(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text, JsonDefaults.Options)
                ?? throw new ArgumentException("옵션은 JSON 객체로 입력하세요.");
        }
        catch (JsonException) { throw new ArgumentException("옵션은 이름과 문자열 값으로 된 JSON 객체로 입력하세요."); }
    }
    private void LoadDeviceSettings(DeviceConfig device)
    {
        DeviceTransportId = device.Connection.TransportId; DeviceEndpoint = device.Connection.Endpoint;
        DeviceAddress = device.Connection.Address;
        DeviceTransportOptions = JsonSerializer.Serialize(device.Connection.Options, JsonDefaults.Options);
        DeviceDriverOptions = JsonSerializer.Serialize(device.DriverOptions, JsonDefaults.Options);
        NotifyDeviceSettings();
    }
    private void NotifyDeviceSettings()
    {
        foreach (var name in new[] { nameof(SelectedModel), nameof(DeviceDriverId), nameof(DeviceTransports),
            nameof(DeviceIsSimulation), nameof(DeviceTransportId), nameof(DeviceEndpoint), nameof(DeviceAddress),
            nameof(DeviceTransportOptions), nameof(DeviceDriverOptions) }) Changed(name);
    }
    private readonly IDeviceSettingsHost _host;
    private Guid _defaultPcId;
    public event Action<string>? RoleAssigned;
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<RoleBinding> Roles { get; } = [];
    public ObservableCollection<DeviceModel> Models { get; } = [];
    public IEnumerable<VirtualFault> Faults => Enum.GetValues<VirtualFault>();
    private DeviceRow? _selectedDevice;
    public DeviceRow? SelectedDevice
    {
        get => _selectedDevice;
        set { var previous = _selectedDevice?.Id; Set(ref _selectedDevice, value); if (previous != value?.Id) LoadAssignedRoleName(); NotifyRoleAssignment(); }
    }
    public AsyncCommand ReconcileCommand { get; }
    public AsyncCommand SaveDeviceCommand { get; }
    public AsyncCommand LoadDeviceCommand { get; }
    public AsyncCommand NewDeviceCommand { get; }
    public AsyncCommand SaveRoleCommand { get; }
    public DeviceSettingsViewModel(IDeviceSettingsHost host, Guid defaultPcId) : base(host)
    {
        _host = host; SetDefaultPc(defaultPcId);
        ReconcileCommand = Command(async () => { await _host.ReconcileAsync(new ReconcileRequest(Generation, SelectedDevice!.Id)); ReportStatus("상태 대조 완료. 과거 불확실 명령의 이력은 그대로 유지됩니다."); }, () => CanControl && SelectedDevice is not null);
        SaveDeviceCommand = Command(SaveDevice, () => CanConfigure);
        LoadDeviceCommand = Command(() => { LoadDevice(); return Task.CompletedTask; }, () => SelectedDevice is not null);
        NewDeviceCommand = Command(() => { DeviceIdText = Guid.NewGuid().ToString(); DeviceExpectedVersion = 0; DeviceName = ""; NotifyEditors(); return Task.CompletedTask; });
        SaveRoleCommand = Command(async () =>
        {
            var sessionId = State?.Session.Id;
            var target = SelectedDevice!;
            var roleId = RoleName.Trim();
            var version = State?.Roles.SingleOrDefault(r => r.Id == roleId)?.Version ?? 0;
            var saved = await _host.SaveRoleAsync(new RoleRequest(Generation, roleId, target.Id, version));
            await _host.RefreshAsync();
            if (State?.Session.Id != sessionId || Context.Closing) return;
            RoleAssigned?.Invoke(saved.Id);
            _roleNameEdited = false; Set(ref _roleName, saved.Id, nameof(RoleName));
            ReportStatus($"역할 배정 완료: {saved.Id} → {target.Name} / {target.PcName}. 장비 제어에서 기능·값을 선택해 명령을 접수하세요.");
        }, () => CanConfigure && SelectedDevice is not null && !string.IsNullOrWhiteSpace(RoleName));
    }
    public void SetDefaultPc(Guid pcId)
    {
        _defaultPcId = pcId; PcIdText = pcId.ToString(); Changed(nameof(PcIdText));
    }
    protected override void OnContextChanged(FeatureContext previous)
    {
        if (previous.State?.Session.Id != State?.Session.Id)
        {
            SelectedDevice = null; _selectedModel = null;
            DeviceIdText = Guid.NewGuid().ToString(); DeviceExpectedVersion = 0; DeviceName = "";
            PcIdText = _defaultPcId.ToString(); PcName = Environment.MachineName; ConnectionId = "";
            DeviceEnabled = true; DeviceFault = VirtualFault.None; DeviceLatencyMs = 50;
            DeviceTransportId = "virtual"; DeviceEndpoint = ""; DeviceAddress = "";
            DeviceTransportOptions = "{}"; DeviceDriverOptions = "{}"; LoadAssignedRoleName();
            NotifyEditors(); NotifyDeviceSettings();
        }
        if (!ReferenceEquals(previous.State, State))
        {
            if (State is not { } state)
            {
                Devices.Clear(); Roles.Clear(); Models.Clear(); SelectedDevice = null; SelectedModel = null;
            }
            else
            {
                var selectedDeviceId = _selectedDevice?.Id; var modelId = SelectedModel?.Id;
                // Keep row identity so polling does not reset selection or an open target picker.
                foreach (var removed in Devices.Where(row => state.Devices.All(d => d.Id != row.Id)).ToArray()) Devices.Remove(removed);
                foreach (var d in state.Devices)
                {
                    var value = state.DeviceStates[d.Id];
                    var reserved = state.Jobs.Any(j => j.Active && j.Kind == JobKind.Scenario && j.Snapshot.Steps.Any(x => x.Target?.Id == d.Id));
                    var row = new DeviceRow(d, string.Join(" / ", value.Values.Select(x => $"{x.Key}={x.Value.Value} ({x.Value.Evidence} · {x.Value.At.ToLocalTime():HH:mm:ss})")),
                        string.Join(" / ", value.Desired.Select(x => $"{x.Key}={x.Value}")), value.Connection, value.LastResult,
                        state.UncertainDevices.Contains(d.Id) ? "대조 필요" : reserved ? "시나리오 예약" : "사용 가능");
                    var existing = Devices.SingleOrDefault(x => x.Id == d.Id);
                    if (existing is null) Devices.Add(row); else existing.Update(row);
                }
                _selectedDevice = Devices.FirstOrDefault(x => x.Id == selectedDeviceId);
                Replace(Roles, state.Roles);
                Replace(Models, state.Models); SelectedModel = Models.FirstOrDefault(x => x.Id == modelId) ?? Models.FirstOrDefault();
                RefreshRoleAssignments();
            }
            Changed(nameof(SelectedDevice)); Changed(nameof(DeviceModeSummary));
        }
        NotifyRoleAssignment();
    }
    private void NotifyEditors()
    {
        foreach (var name in new[] { nameof(DeviceIdText), nameof(PcIdText), nameof(PcName), nameof(DeviceName), nameof(ConnectionId),
            nameof(SelectedModel), nameof(DeviceEnabled), nameof(DeviceFault), nameof(DeviceLatencyMs), nameof(DeviceExpectedVersion) }) Changed(name);
    }
    private void NotifyRoleAssignment()
    {
        Changed(nameof(RoleTargetSummary)); Changed(nameof(RoleAssignmentHint));
        RefreshAssignedRoleRows(); RefreshCommands();
    }
    public string DeviceIdText { get; set; } = Guid.NewGuid().ToString();
    public string PcIdText { get; set; } = "";
    public string PcName { get; set; } = Environment.MachineName;
    public string DeviceName { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public bool DeviceEnabled { get; set; } = true;
    public VirtualFault DeviceFault { get; set; }
    public int DeviceLatencyMs { get; set; } = 50;
    public int DeviceExpectedVersion { get; private set; }
    private string _roleName = "";
    public string RoleName { get => _roleName; set { if (Set(ref _roleName, value)) { _roleNameEdited = true; NotifyRoleAssignment(); } } }
    public string RoleTargetSummary => SelectedDevice is null ? "배정 대상: 장비를 선택하세요." :
        $"배정 대상: {SelectedDevice.Name} / {SelectedDevice.PcName}\n장비 ID: {SelectedDevice.Id}";
    public string RoleAssignmentHint => !IsLoggedIn ? "호스트에 접속한 뒤 관리자 계정으로 사용 시작을 누르세요." :
        !IsAdmin ? "역할 배정은 관리자만 할 수 있습니다." : !CanControl ? "역할 배정에는 사용권이 필요합니다. 사용 시작을 누르세요." :
        SelectedDevice is null ? "목록에서 배정할 장비를 먼저 선택하세요." : string.IsNullOrWhiteSpace(RoleName) ?
        "역할 ID를 입력하세요. 예: room.light" : "배정 후 장비 제어의 역할로 조작에서 기능과 값을 선택하세요. 동일 역할 ID는 선택 장비로 재배정됩니다.";
    private async Task SaveDevice()
    {
        if (SelectedModel is null) throw new ArgumentException("장비 모델을 선택하세요.");
        var sessionId = State?.Session.Id;
        var device = await _host.SaveDeviceAsync(new DeviceRequest(Generation, Guid.Parse(DeviceIdText),
            Guid.Parse(PcIdText), PcName, DeviceName, ConnectionId, SelectedModel.Id, DeviceEnabled,
            DeviceIsSimulation ? DeviceFault : VirtualFault.None, DeviceIsSimulation ? DeviceLatencyMs : 0, DeviceExpectedVersion)
        {
            DriverId = DeviceDriverId,
            Connection = new(DeviceTransportId, DeviceEndpoint.Trim(), DeviceAddress.Trim()) { Options = ParseDeviceOptions(DeviceTransportOptions) },
            DriverOptions = ParseDeviceOptions(DeviceDriverOptions)
        });
        if (State?.Session.Id != sessionId) return;
        DeviceExpectedVersion = device.Version; ReportStatus("장비 설정을 저장했습니다."); NotifyEditors();
    }
    private void LoadDevice()
    {
        var d = SelectedDevice!.Config;
        DeviceIdText = d.Id.ToString(); PcIdText = d.PcId.ToString(); PcName = d.PcName; DeviceName = d.Name;
        ConnectionId = d.ConnectionId; SelectedModel = Models.Single(x => x.Id == d.ModelId); DeviceEnabled = d.Enabled;
        DeviceFault = d.Fault; DeviceLatencyMs = d.LatencyMs; DeviceExpectedVersion = d.Version; LoadDeviceSettings(d); NotifyEditors();
    }
}
