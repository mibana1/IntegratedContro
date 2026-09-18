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
            if (Equals(_selectedModel, value)) return;
            var previous = _selectedModel?.Id;
            _selectedModel = value;
            if (value is not null && previous != value.Id && !DeviceTransports.Contains(DeviceTransportId))
                DeviceTransportId = DeviceTransports.FirstOrDefault() ?? "";
            Changed(nameof(SelectedModel)); Changed(nameof(DeviceDriverId));
            Changed(nameof(DeviceTransports)); Changed(nameof(DeviceIsSimulation));
            Changed(nameof(DeviceTransportId));
            ModelSettingsChanged();
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
            if (!string.IsNullOrWhiteSpace(value) && _deviceTransportId != value)
            {
                if (!_loadingConnection) CaptureFieldDrafts(validate: false);
                Set(ref _deviceTransportId, value); TransportSettingsChanged();
            }
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
        RebuildFields();
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
        set { var previous = _selectedDevice?.Id; Set(ref _selectedDevice, value); if (previous != value?.Id) LoadAssignedRoleName(); NotifyRoleAssignment(); Changed(nameof(DiagnosticsSummary)); RefreshDiagnostics(); }
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
        NewDeviceCommand = Command(() => { NewDevice(); return Task.CompletedTask; });
        InitializeConfiguration();
        InitializeRoleManagement();
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
            ReportStatus($"역할 배정 완료: {target.Name}. 장비 제어에서 기능·값을 선택해 명령을 접수하세요.");
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
            DeviceIdText = Guid.NewGuid().ToString(); DeviceExpectedVersion = 0; DeviceName = ""; _editingDeviceId = Guid.Parse(DeviceIdText); ResetConfiguration();
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
                        state.UncertainDevices.Contains(d.Id) ? "대조 필요" : reserved ? "시나리오 예약" : "사용 가능")
                        { IsSimulation = state.Models.Any(m => m.Id == d.ModelId && m.IsSimulation) };
                    var existing = Devices.SingleOrDefault(x => x.Id == d.Id);
                    if (existing is null) Devices.Add(row); else existing.Update(row);
                }
                _selectedDevice = Devices.FirstOrDefault(x => x.Id == selectedDeviceId);
                Replace(Roles, state.Roles);
                Replace(Models, state.Models); SelectedModel = Models.FirstOrDefault(x => x.Id == modelId) ?? Models.FirstOrDefault();
                RefreshRoleAssignments();
            }
            RefreshConfigurationCatalog();
            Changed(nameof(SelectedDevice)); Changed(nameof(DeviceModeSummary)); Changed(nameof(DiagnosticsSummary));
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
        RefreshAssignedRoleRows(); RefreshRoleManagement(); RefreshCommands();
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
    public string RoleTargetSummary => SelectedDevice is null ? "배정 대상: 장비를 선택하세요." : $"배정 대상: {SelectedDevice.Name}";
    public string RoleAssignmentHint => !CanConfigure ? "역할 변경에는 관리자 사용권이 필요합니다." :
        SelectedDevice is null ? "배정할 장비를 선택하세요." : "이름을 입력해 새 역할을 생성하거나 기존 역할을 선택해 이어받을 수 있습니다. 역할 하나는 장비 한 대를 가리킵니다.";
    private async Task SaveDevice()
    {
        if (SelectedModel is null) throw new ArgumentException("장비 모델을 선택하세요.");
        var sessionId = State?.Session.Id;
        var modern = State?.DeviceConfigurationSupported == true;
        var connection = ReadConnectionFields();
        var request = new DeviceRequest(Generation, modern ? _editingDeviceId : Guid.Parse(DeviceIdText),
            RequiresTargetPc ? SelectedTargetPc?.Id ?? Guid.Empty : Guid.Parse(PcIdText), PcName, DeviceName,
            modern ? SelectedConnection?.Id ?? "" : ConnectionId, SelectedModel.Id, DeviceEnabled,
            DeviceFault, DeviceLatencyMs, DeviceExpectedVersion)
        {
            DriverId = DeviceDriverId, Connection = connection, DriverOptions = ReadDriverFields(),
            ConnectionMode = modern ? ConnectionMode : null,
            ConnectionName = string.IsNullOrWhiteSpace(ConnectionName) ? $"{DeviceName.Trim()} 연결" : ConnectionName,
            ExpectedConnectionVersion = _connectionExpectedVersion, Location = DeviceLocation
        };
        if (modern && ConnectionMode == ConnectionSaveMode.Existing && HasSharedDraftChanges(connection))
            throw new ArgumentException("공유 설정을 변경하려면 ‘공유 연결 수정’ 또는 ‘이 장비용 새 연결로 저장’을 선택하세요.");
        var device = await _host.SaveDeviceAsync(request);
        if (State?.Session.Id != sessionId) return;
        _editingDeviceId = device.Id; DeviceIdText = device.Id.ToString();
        DeviceExpectedVersion = device.Version;
        if (modern) { await _host.RefreshAsync(); if (State?.Session.Id != sessionId) return; LoadConnection(device); }
        ReportStatus("장비 설정을 저장했습니다. 새 장비는 기본 역할로 바로 조작할 수 있습니다."); NotifyEditors();
    }
    private void LoadDevice()
    {
        var d = SelectedDevice!.Config;
        _editingDeviceId = d.Id; DeviceLocation = d.Location;
        DeviceIdText = d.Id.ToString(); PcIdText = d.PcId.ToString(); PcName = d.PcName; DeviceName = d.Name;
        ConnectionId = d.ConnectionId; SelectedModel = Models.Single(x => x.Id == d.ModelId); DeviceEnabled = d.Enabled;
        DeviceFault = d.Fault; DeviceLatencyMs = d.LatencyMs; DeviceExpectedVersion = d.Version; LoadDeviceSettings(d); LoadConnection(d); NotifyEditors();
    }
}
