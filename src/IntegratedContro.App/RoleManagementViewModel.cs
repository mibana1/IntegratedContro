using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class DeviceSettingsViewModel
{
    private RoleChoice? _managedRole;
    private RoleBinding? _roleEditSnapshot;
    private bool _roleEditAssigned;
    private bool _refreshingRoleChoices;
    private string _roleDisplayName = "";
    private string _newUnassignedRoleName = "";
    public string NewUnassignedRoleName
    {
        get => _newUnassignedRoleName;
        set { if (Set(ref _newUnassignedRoleName, value)) RefreshCommands(); }
    }
    public AsyncCommand CreateUnassignedRoleCommand { get; private set; } = null!;
    public string RoleCreationHint => State?.UnassignedRoleCreationSupported != true
        ? "장비 없이 역할을 생성하려면 최신 ControlHost가 필요합니다."
        : !CanConfigure ? "역할 생성에는 관리자 사용권이 필요합니다."
        : "장비 선택 없이 생성할 수 있습니다. 만든 역할은 아래에서 장비에 배정하세요.";
    public RoleChoice? ManagedRole
    {
        get => _managedRole;
        set
        {
            if (_refreshingRoleChoices || !Set(ref _managedRole, value)) return;
            LoadRoleEditor();
        }
    }
    public string RoleDisplayName
    {
        get => _roleDisplayName;
        set { if (Set(ref _roleDisplayName, value)) RefreshCommands(); }
    }
    public AsyncCommand ReloadRoleCommand { get; private set; } = null!;
    public AsyncCommand RenameRoleCommand { get; private set; } = null!;
    public AsyncCommand DeleteRoleCommand { get; private set; } = null!;
    private bool RoleEditorCurrent => _roleEditSnapshot is { } original && ManagedRole?.Binding == original &&
        _roleEditAssigned == (State?.Roles.Any(r => r.Id == original.Id) == true);
    private bool CanManageRole => CanConfigure && State?.RoleManagementSupported == true && RoleEditorCurrent;
    private bool ManagedRoleReferenced => ManagedRole is { } selected &&
        State?.Scenarios.Any(s => s.Steps.Any(step => step.RoleId == selected.Id)) == true;
    public string RoleManagementHint => State?.RoleManagementSupported != true ? "역할 수정·삭제에는 최신 ControlHost가 필요합니다." :
        !CanConfigure ? "역할 수정·삭제에는 관리자 사용권이 필요합니다." :
        ManagedRole is null ? "수정하거나 삭제할 역할을 선택하세요. 미배정 역할도 관리할 수 있습니다." :
        !RoleEditorCurrent ? "역할 정보가 변경되었습니다. 다시 불러온 뒤 수정하거나 삭제하세요." :
        _roleEditAssigned ? "장비에 배정된 역할입니다. 이름은 수정할 수 있으며, 삭제하려면 먼저 역할 배정을 해제하세요." :
        RoleInUse(ManagedRole.Binding) ? "진행 중인 작업에서 사용 중입니다. 이름은 수정할 수 있으며, 삭제는 작업 완료 또는 취소 후 가능합니다." :
        ManagedRoleReferenced ? "사용 시나리오: " + string.Join(", ", State!.Scenarios.Where(s => s.Steps.Any(step => step.RoleId == ManagedRole.Id)).Select(s => s.Name)) +
            " · 삭제하려면 해당 단계의 역할을 변경하거나 시나리오를 삭제하세요." :
        "이름 수정은 시나리오 연결을 유지합니다. 역할을 삭제해도 장비와 실행 이력은 유지됩니다.";

    private void InitializeRoleManagement()
    {
        CreateUnassignedRoleCommand = Command(async () =>
        {
            var session = State!.Session.Id;
            var name = NewUnassignedRoleName;
            var saved = await _host.CreateRoleAsync(new(Generation, name));
            await _host.RefreshAsync();
            if (State?.Session.Id != session || Context.Closing) return;
            if (NewUnassignedRoleName == name) NewUnassignedRoleName = "";
            ManagedRole = RoleChoices.FirstOrDefault(r => r.Id == saved.Id);
            SelectedRoleToInherit = ManagedRole;
            Changed(nameof(SelectedRoleToInherit));
            ReportStatus($"‘{saved.Name}’ 역할을 생성했습니다. 배정할 장비를 선택하세요.");
        }, () => CanConfigure && State?.UnassignedRoleCreationSupported == true && !string.IsNullOrWhiteSpace(NewUnassignedRoleName));

        ReloadRoleCommand = LocalCommand(LoadRoleEditor, () => ManagedRole is not null);
        RenameRoleCommand = Command(async () =>
        {
            var session = State!.Session.Id;
            var original = _roleEditSnapshot!;
            var saved = await _host.RenameRoleAsync(new(Generation, original, _roleEditAssigned, RoleDisplayName));
            await _host.RefreshAsync();
            if (State?.Session.Id != session || Context.Closing) return;
            if (ManagedRole?.Id == saved.Id) LoadRoleEditor();
            ReportStatus("역할 이름을 수정했습니다.");
        }, () => CanManageRole && !string.IsNullOrWhiteSpace(RoleDisplayName) &&
            RoleDisplayName.Trim() != RoleEditorName(_roleEditSnapshot!));
        DeleteRoleCommand = Command(async () =>
        {
            var session = State!.Session.Id;
            await _host.DeleteRoleAsync(new(Generation, _roleEditSnapshot!, _roleEditAssigned));
            await _host.RefreshAsync();
            if (State?.Session.Id != session || Context.Closing) return;
            ReportStatus("역할을 삭제했습니다. 장비와 실행 이력은 유지됩니다.");
        }, () => CanManageRole && !_roleEditAssigned && !RoleInUse(ManagedRole!.Binding) && !ManagedRoleReferenced);
    }
    private string RoleEditorName(RoleBinding role) => role.IsDefault
        ? State?.Devices.FirstOrDefault(d => d.Id == role.DeviceId)?.Name ?? role.Name
        : role.Name;
    private void LoadRoleEditor()
    {
        _roleEditSnapshot = ManagedRole?.Binding;
        _roleEditAssigned = State?.Roles.Any(r => r.Id == _roleEditSnapshot?.Id) == true;
        RoleDisplayName = _roleEditSnapshot is null ? "" : RoleEditorName(_roleEditSnapshot);
        Changed(nameof(RoleManagementHint)); RefreshCommands();
    }
    private void ResetRoleEditor()
    {
        _managedRole = null; _roleEditSnapshot = null; _roleEditAssigned = false; RoleDisplayName = "";
        Changed(nameof(ManagedRole)); Changed(nameof(RoleManagementHint));
    }
    private void RefreshRoleManagement()
    {
        Changed(nameof(RoleCreationHint));
        if (_managedRole is { } selected)
        {
            _managedRole = RoleChoices.FirstOrDefault(r => r.Id == selected.Id);
            if (_managedRole is null) ResetRoleEditor();
            Changed(nameof(ManagedRole));
        }
        Changed(nameof(RoleManagementHint));
    }
}
