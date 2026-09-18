using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class DeviceSettingsViewModel
{
    private Guid _editingDeviceId;
    private bool _loadingConnection;
    private int _connectionExpectedVersion;
    private SharedDeviceConnection? _selectedConnection;
    public ObservableCollection<SharedDeviceConnection> Connections { get; } = [];
    public ObservableCollection<PcRegistration> Pcs { get; } = [];
    public ObservableCollection<RoleChoice> RoleChoices { get; } = [];
    public ObservableCollection<DeviceConfigurationField> SharedFields { get; } = [];
    public ObservableCollection<DeviceConfigurationField> DeviceFields { get; } = [];
    public string DeviceLocation { get; set; } = "";
    private string _connectionName = "";
    public string ConnectionName { get => _connectionName; set => Set(ref _connectionName, value); }
    private bool _allConnections;
    public bool ShowAllConnections { get => _allConnections; set { if (Set(ref _allConnections, value)) Changed(nameof(AvailableConnections)); } }
    public IEnumerable<SharedDeviceConnection> AvailableConnections => Connections.Where(c => ShowAllConnections ||
        (SelectedModel is { } model && IsCompatibleConnection(model, c) && c.Settings.TransportId == DeviceTransportId));
    public static bool IsCompatibleConnection(DeviceModel model, SharedDeviceConnection connection) =>
        model.TransportIds.Contains(connection.Settings.TransportId) &&
        (!model.ExecutionPcTransportIds.Contains(connection.Settings.TransportId) || connection.Settings.ExecutionPcId is not null) &&
        model.Settings.Where(f => f.IsShared && (f.TransportId is null || f.TransportId == connection.Settings.TransportId)).All(f =>
            f.Accepts(f.Target == DeviceSettingTarget.Endpoint ? connection.Settings.Endpoint : connection.Settings.Options.GetValueOrDefault(f.Key, "")));
    private ConnectionSaveMode _connectionMode = ConnectionSaveMode.Create;
    public ConnectionSaveMode ConnectionMode { get => _connectionMode; private set { Set(ref _connectionMode, value); NotifyConnection(); } }
    public SharedDeviceConnection? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            // Catalog refreshes may clear WPF selection. An explicit new-connection action clears the draft.
            if (value is null || _loadingConnection) return;
            CaptureFieldDrafts(validate: false);
            _selectedConnection = value; _connectionExpectedVersion = value.Version; ConnectionId = value.Id;
            _loadingConnection = true;
            if (SelectedModel is null || !IsCompatibleConnection(SelectedModel, value))
                SelectedModel = Models.FirstOrDefault(m => IsCompatibleConnection(m, value));
            DeviceTransportId = value.Settings.TransportId; DeviceEndpoint = value.Settings.Endpoint;
            DeviceTransportOptions = System.Text.Json.JsonSerializer.Serialize(value.Settings.Options, JsonDefaults.Options);
            ExecutionPc = Pcs.FirstOrDefault(p => p.Id == value.Settings.ExecutionPcId);
            ConnectionName = value.Name; _loadingConnection = false;
            ConnectionMode = ConnectionSaveMode.Existing; NotifyDeviceSettings(); NotifyConnection();
        }
    }
    public bool HasSelectedConnection => SelectedConnection is not null;
    public bool CanEditSharedFields => ConnectionMode != ConnectionSaveMode.Existing;
    public bool RequiresTargetPc => SelectedModel?.RequiresTargetPc == true;
    public bool RequiresExecutionPc => SelectedModel?.ExecutionPcTransportIds.Contains(DeviceTransportId) == true;
    public bool ShowExecutionPc => RequiresExecutionPc || ExecutionPc is not null;
    private bool _refreshingCatalog;
    private PcRegistration? _targetPc;
    private PcRegistration? _executionPc;
    public PcRegistration? SelectedTargetPc { get => _targetPc; set { if (!_refreshingCatalog) Set(ref _targetPc, value); } }
    public PcRegistration? ExecutionPc { get => _executionPc; set { if (!_refreshingCatalog && Set(ref _executionPc, value)) Changed(nameof(ShowExecutionPc)); } }
    public string ConnectionModeSummary => ConnectionMode switch
    {
        ConnectionSaveMode.Existing => "기존 공유 연결 사용",
        ConnectionSaveMode.UpdateShared => "공유 연결 수정 · 아래 장비에 함께 반영",
        _ => "이 장비용 새 연결 생성"
    };
    public string ConnectionImpact => SelectedConnection is null ? "새 연결은 다른 장비와 자동으로 합치지 않습니다." :
        "이 연결을 사용하는 장비: " + string.Join(", ", State?.Devices.Where(d => d.ConnectionId == SelectedConnection.Id).Select(d => d.Name) ?? []) +
        "\n접수된 작업은 대상을 유지하며, 실행 설정이 바뀌면 다음 전송 전에 중단·재검토합니다.";
    public AsyncCommand NewConnectionCommand { get; private set; } = null!;
    public AsyncCommand EditSharedConnectionCommand { get; private set; } = null!;
    public AsyncCommand CloneConnectionCommand { get; private set; } = null!;
    public AsyncCommand ReloadConnectionCommand { get; private set; } = null!;
    public AsyncCommand RegisterPcCommand { get; private set; } = null!;
    public AsyncCommand SaveDiagnosticsCommand { get; private set; } = null!;
    public AsyncCommand LoadDiagnosticsCommand { get; private set; } = null!;
    public AsyncCommand CreateRoleCommand { get; private set; } = null!;
    public AsyncCommand InheritRoleCommand { get; private set; } = null!;
    private string _newRoleDisplayName = "";
    public string NewRoleDisplayName
    {
        get => _newRoleDisplayName;
        set { if (Set(ref _newRoleDisplayName, value)) RefreshCommands(); }
    }
    public RoleChoice? SelectedRoleToInherit { get; set; }
    public VirtualFault DiagnosticFault { get; set; }
    public string DiagnosticLatencyText { get; set; } = "50";
    private int _diagnosticVersion;
    private Guid _diagnosticDeviceId;
    public bool CanSetDiagnostics => SelectedDevice is not null && State?.Models.Any(m => m.Id == SelectedDevice.Config.ModelId && m.IsSimulation) == true;
    public string DiagnosticsSummary => SelectedDevice is not { } d ? "진단할 장비를 선택하세요." :
        $"{d.Name}\n장비 ID: {d.Id}\nPC ID: {d.Config.PcId}\n저장된 PC 이름: {d.Config.PcName}\n연결 ID: {d.Config.ConnectionId}" +
        (d.Config.LegacyConnectionId is { } legacy ? $"\n이전 연결 ID: {legacy}" : "") +
        $"\n가상 진단: 장애 {d.Config.Fault} · 지연 {d.Config.LatencyMs}ms";

    private void InitializeConfiguration()
    {
        _editingDeviceId = Guid.Parse(DeviceIdText);
        NewConnectionCommand = Command(() => { StartNewConnection(false); return Task.CompletedTask; });
        CloneConnectionCommand = Command(() => { StartNewConnection(true); return Task.CompletedTask; }, () => HasSelectedConnection);
        EditSharedConnectionCommand = Command(() => { ConnectionMode = ConnectionSaveMode.UpdateShared; return Task.CompletedTask; }, () => HasSelectedConnection);
        ReloadConnectionCommand = Command(() =>
        {
            var current = Connections.FirstOrDefault(c => c.Id == SelectedConnection?.Id);
            if (current is null) throw new ArgumentException("연결이 없어졌습니다. 새 연결을 선택하세요.");
            SelectedConnection = current; return Task.CompletedTask;
        }, () => HasSelectedConnection);
        RegisterPcCommand = Command(async () =>
        {
            await _host.RegisterSessionPcAsync(new(Generation)); await _host.RefreshAsync();
            ReportStatus("이 앱 PC의 등록 정보를 저장했습니다. 대상 PC와 통신 실행 PC는 별도로 선택하세요.");
        }, () => CanConfigure && State?.DeviceConfigurationSupported == true);
        LoadDiagnosticsCommand = Command(() => { LoadDiagnostics(); return Task.CompletedTask; }, () => CanSetDiagnostics);
        SaveDiagnosticsCommand = Command(async () =>
        {
            if (!int.TryParse(DiagnosticLatencyText, out var latency) || latency is < 0 or > 30000)
                throw new ArgumentException("가상 지연은 0~30000ms 정수로 입력하세요.");
            var saved = await _host.SaveDeviceDiagnosticsAsync(new(Generation, _diagnosticDeviceId, _diagnosticVersion, DiagnosticFault, latency));
            _diagnosticVersion = saved.Version; await _host.RefreshAsync(); Changed(nameof(DiagnosticsSummary));
            ReportStatus("가상 진단 설정을 저장했습니다. 장비 상태에서 장애 주입 여부를 확인할 수 있습니다.");
        }, () => CanConfigure && CanSetDiagnostics && _diagnosticDeviceId == SelectedDevice?.Id);
        CreateRoleCommand = Command(async () =>
        {
            var session = State!.Session.Id;
            var name = NewRoleDisplayName;
            var target = SelectedDevice!;
            var saved = await _host.SaveRoleAsync(new(Generation, "", target.Id) { Name = name });
            await _host.RefreshAsync();
            if (State?.Session.Id != session || Context.Closing) return;
            if (NewRoleDisplayName == name) NewRoleDisplayName = "";
            ManagedRole = RoleChoices.FirstOrDefault(r => r.Id == saved.Id);
            RoleAssigned?.Invoke(saved.Id);
            ReportStatus($"‘{saved.Name}’ 역할을 생성하고 {target.Name}에 추가했습니다.");
        }, () => CanConfigure && SelectedDevice is { Config.Enabled: true } && !string.IsNullOrWhiteSpace(NewRoleDisplayName));
        InheritRoleCommand = Command(async () =>
        {
            var role = SelectedRoleToInherit?.Binding ?? throw new ArgumentException("이어받을 역할을 선택하세요.");
            await _host.SaveRoleAsync(new(Generation, role.Id, SelectedDevice!.Id, State!.Roles.Any(r => r.Id == role.Id) ? role.Version : 0));
            await _host.RefreshAsync(); RoleAssigned?.Invoke(role.Id); ReportStatus("기존 역할을 선택 장비에 배정했습니다. 저장된 시나리오 연결은 유지됩니다.");
        }, () => CanConfigure && SelectedDevice is not null);
    }
    private void NewDevice()
    {
        _editingDeviceId = Guid.NewGuid(); DeviceIdText = _editingDeviceId.ToString(); DeviceExpectedVersion = 0;
        DeviceName = ""; DeviceLocation = ""; DeviceEnabled = true; DeviceFault = VirtualFault.None; DeviceLatencyMs = 50;
        PcIdText = _defaultPcId.ToString(); PcName = ""; DeviceAddress = ""; DeviceDriverOptions = "{}";
        SelectedTargetPc = null; SharedFields.Clear(); DeviceFields.Clear(); StartNewConnection(false); NotifyEditors(); Changed(nameof(DeviceLocation));
    }
    private void ResetConfiguration()
    {
        _selectedConnection = null; _connectionExpectedVersion = 0; _connectionMode = ConnectionSaveMode.Create;
        ConnectionName = ""; DeviceLocation = ""; ExecutionPc = null; SelectedTargetPc = null; _allConnections = false;
        NewRoleDisplayName = ""; NewUnassignedRoleName = ""; SelectedRoleToInherit = null; ResetRoleEditor(); SharedFields.Clear(); DeviceFields.Clear();
        NotifyConnection(); Changed(nameof(DeviceLocation));
    }
    private void StartNewConnection(bool copy)
    {
        CaptureFieldDrafts(validate: false);
        _selectedConnection = null; _connectionExpectedVersion = 0; ConnectionId = "";
        ConnectionMode = ConnectionSaveMode.Create; ConnectionName = copy ? $"{ConnectionName} 복사" : "";
        if (!copy) { DeviceEndpoint = ""; DeviceTransportOptions = "{}"; ExecutionPc = null; }
        RebuildFields(); NotifyConnection();
    }
    private void ModelSettingsChanged()
    {
        if (_loadingConnection) return;
        if (SelectedConnection is { } selected && SelectedModel is { } model && !IsCompatibleConnection(model, selected))
            StartNewConnection(false);
        RebuildFields(); NotifyConnection();
    }
    private void TransportSettingsChanged()
    {
        if (_loadingConnection) return;
        if (SelectedConnection is { } selected && selected.Settings.TransportId != DeviceTransportId) StartNewConnection(false);
        RebuildFields(); NotifyConnection();
    }
    private void LoadConnection(DeviceConfig device)
    {
        _loadingConnection = true;
        _selectedConnection = Connections.FirstOrDefault(c => c.Id == device.ConnectionId);
        _connectionExpectedVersion = _selectedConnection?.Version ?? 0;
        _connectionMode = _selectedConnection is null ? ConnectionSaveMode.Create : ConnectionSaveMode.Existing;
        ConnectionName = _selectedConnection?.Name ?? $"{device.Name} 연결";
        ExecutionPc = Pcs.FirstOrDefault(p => p.Id == device.Connection.ExecutionPcId);
        SelectedTargetPc = Pcs.FirstOrDefault(p => p.Id == device.PcId);
        _loadingConnection = false; RebuildFields(); NotifyConnection(); Changed(nameof(DeviceLocation));
    }
    private void RefreshConfigurationCatalog()
    {
        var targetId = SelectedTargetPc?.Id; var executionId = ExecutionPc?.Id;
        _refreshingCatalog = true;
        Replace(Connections, State?.DeviceConnections ?? []); Replace(Pcs, State?.Pcs ?? []);
        _refreshingCatalog = false;
        SelectedTargetPc = Pcs.FirstOrDefault(p => p.Id == targetId); ExecutionPc = Pcs.FirstOrDefault(p => p.Id == executionId);
        var inherited = SelectedRoleToInherit?.Id;
        _refreshingRoleChoices = true;
        Replace(RoleChoices, (State?.Roles ?? []).Select(r => RoleChoice.From(r, State?.Devices ?? []))
            .Concat((State?.UnassignedRoles ?? []).Select(r => new RoleChoice(r, $"미배정 · {(r.Name.Length > 0 ? r.Name : RoleChoice.From(r, State?.Devices ?? []).Label)}"))));
        _refreshingRoleChoices = false;
        SelectedRoleToInherit = RoleChoices.FirstOrDefault(r => r.Id == inherited);
        NotifyConnection();
    }
    private void NotifyConnection()
    {
        foreach (var name in new[] { nameof(SelectedConnection), nameof(AvailableConnections), nameof(HasSelectedConnection),
            nameof(CanEditSharedFields), nameof(ConnectionModeSummary), nameof(ConnectionImpact), nameof(RequiresTargetPc),
            nameof(RequiresExecutionPc), nameof(ShowExecutionPc), nameof(ExecutionPc), nameof(SelectedTargetPc), nameof(ShowAllConnections),
            nameof(SelectedRoleToInherit), nameof(CanSetDiagnostics) }) Changed(name);
        RefreshCommands();
    }
    private void RebuildFields()
    {
        if (_loadingConnection) return;
        var shared = ParseDeviceOptions(DeviceTransportOptions); var options = ParseDeviceOptions(DeviceDriverOptions);
        SharedFields.Clear(); DeviceFields.Clear();
        foreach (var definition in SelectedModel?.Settings.Where(f => f.TransportId is null || f.TransportId == DeviceTransportId) ?? [])
        {
            var value = definition.Target switch
            {
                DeviceSettingTarget.Endpoint => DeviceEndpoint.Length == 0 ? null : DeviceEndpoint,
                DeviceSettingTarget.Address => DeviceAddress.Length == 0 ? null : DeviceAddress,
                DeviceSettingTarget.ConnectionOption => shared.GetValueOrDefault(definition.Key),
                _ => options.GetValueOrDefault(definition.Key)
            };
            (definition.IsShared ? SharedFields : DeviceFields).Add(new(definition, value));
        }
    }
    private void CaptureFieldDrafts(bool validate = true)
    {
        var shared = ParseDeviceOptions(DeviceTransportOptions); var options = ParseDeviceOptions(DeviceDriverOptions);
        foreach (var field in SharedFields.Concat(DeviceFields))
        {
            if (validate && field.Error.Length > 0) throw new ArgumentException(field.Error);
            switch (field.Definition.Target)
            {
                case DeviceSettingTarget.Endpoint: DeviceEndpoint = field.Value; break;
                case DeviceSettingTarget.Address: DeviceAddress = field.Value; break;
                case DeviceSettingTarget.ConnectionOption: shared[field.Definition.Key] = field.Value; break;
                case DeviceSettingTarget.DriverOption: options[field.Definition.Key] = field.Value; break;
            }
        }
        DeviceTransportOptions = System.Text.Json.JsonSerializer.Serialize(shared, JsonDefaults.Options);
        DeviceDriverOptions = System.Text.Json.JsonSerializer.Serialize(options, JsonDefaults.Options);
    }
    private DeviceConnection ReadConnectionFields()
    {
        CaptureFieldDrafts();
        return new(DeviceTransportId, DeviceEndpoint.Trim(), DeviceAddress.Trim())
        { Options = ParseDeviceOptions(DeviceTransportOptions), ExecutionPcId = ExecutionPc?.Id };
    }
    private Dictionary<string, string> ReadDriverFields() => ParseDeviceOptions(DeviceDriverOptions);
    private bool HasSharedDraftChanges(DeviceConnection value) => SelectedConnection is { } selected &&
        (!selected.Settings.HasSameSettings(DeviceConfigurationStorage.SharedSettings(value)) || selected.Name != ConnectionName);
    private void RefreshDiagnostics()
    {
        _diagnosticDeviceId = Guid.Empty; Changed(nameof(CanSetDiagnostics)); RefreshCommands();
    }
    private void LoadDiagnostics()
    {
        var d = SelectedDevice!.Config; _diagnosticDeviceId = d.Id; _diagnosticVersion = d.Version;
        DiagnosticFault = d.Fault; DiagnosticLatencyText = d.LatencyMs.ToString();
        Changed(nameof(DiagnosticFault)); Changed(nameof(DiagnosticLatencyText)); RefreshCommands();
    }
}
