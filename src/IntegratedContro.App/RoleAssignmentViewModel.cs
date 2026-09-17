using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class RoleAssignmentRow(RoleBinding binding, AsyncCommand unassignCommand, Func<string> hint) : Bindable
{
    public RoleBinding Binding { get; } = binding;
    public string Id => Binding.Id;
    public AsyncCommand UnassignCommand { get; } = unassignCommand;
    public string Hint => hint();
    public void Refresh() { Changed(nameof(Hint)); UnassignCommand.Raise(); }
}

public sealed partial class DeviceSettingsViewModel
{
    public ObservableCollection<RoleAssignmentRow> AssignedRoles { get; } = [];
    private bool _roleNameEdited;
    public string AssignedRoleSummary => SelectedDevice is null ? "등록된 역할 ID: 장비를 선택하세요." :
        "등록된 역할 ID: " + (Roles.Any(r => r.DeviceId == SelectedDevice.Id)
            ? string.Join(", ", Roles.Where(r => r.DeviceId == SelectedDevice.Id).Select(r => r.Id)) : "없음");
    private void LoadAssignedRoleName()
    {
        _roleNameEdited = false;
        Set(ref _roleName, Roles.FirstOrDefault(r => r.DeviceId == SelectedDevice?.Id)?.Id ?? "", nameof(RoleName));
        Changed(nameof(AssignedRoleSummary));
    }
    private void RefreshRoleAssignments()
    {
        if (!_roleNameEdited && !Roles.Any(r => r.DeviceId == SelectedDevice?.Id && r.Id == _roleName)) LoadAssignedRoleName();
        Changed(nameof(AssignedRoleSummary));
    }
    private bool RoleInUse(RoleBinding role) => State?.Jobs.Any(j => j.Active &&
        j.Snapshot.Steps.Any(s => s.Role?.Id == role.Id)) == true;
    private string RoleUnassignmentHint(RoleBinding role)
    {
        if (State?.RoleUnassignmentSupported != true) return "배정 해제에는 최신 ControlHost가 필요합니다.";
        if (RoleInUse(role)) return "진행 중 작업에서 사용 중입니다. 작업·교대에서 완료를 확인하거나 취소한 뒤 해제하세요.";
        var definitions = State.Scenarios.Where(s => s.Steps.Any(step => step.RoleId == role.Id)).ToArray();
        return definitions.Length == 0 ? "장비는 유지되고 이 역할의 배정만 해제합니다." :
            $"사용 시나리오: {string.Join(", ", definitions.Take(3).Select(s => s.Name))}" +
            (definitions.Length > 3 ? $" 외 {definitions.Length - 3}개" : "") +
            " · 해제하면 재배정 전까지 실행할 수 없습니다.";
    }
    private void RefreshAssignedRoleRows()
    {
        var roles = Roles.Where(r => r.DeviceId == SelectedDevice?.Id).ToArray();
        foreach (var row in AssignedRoles.Where(row => !roles.Contains(row.Binding)).ToArray())
        {
            AssignedRoles.Remove(row); row.UnassignCommand.Raise();
        }
        foreach (var role in roles)
            if (!AssignedRoles.Any(row => row.Binding == role))
                AssignedRoles.Add(new(role, Command(() => UnassignRole(role), () =>
                    CanConfigure && State?.RoleUnassignmentSupported == true &&
                    SelectedDevice?.Id == role.DeviceId && Roles.Contains(role) && !RoleInUse(role), register: false),
                    () => RoleUnassignmentHint(role)));
        foreach (var row in AssignedRoles) row.Refresh();
    }
    private async Task UnassignRole(RoleBinding role)
    {
        await _host.UnassignRoleAsync(new UnassignRoleRequest(Generation, role.Id, role.DeviceId, role.Version));
        await _host.RefreshAsync();
        if (SelectedDevice?.Id == role.DeviceId && RoleName.Trim() == role.Id) LoadAssignedRoleName();
        ReportStatus($"역할 배정 해제 완료: {role.Id}. 장비와 저장된 시나리오 정의는 유지됩니다.");
    }
}
