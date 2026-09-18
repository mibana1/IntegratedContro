using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

internal sealed partial class DeviceExecutionService
{
    public RoleBinding CreateRole(string token, CreateRoleRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Text(request.Name, "역할 이름");
        var role = new RoleBinding(Guid.NewGuid().ToString("N"), Guid.Empty, 0) { Name = request.Name.Trim() };
        s.UnassignedRoles.Add(role);
        _host.Audit(s, session.Info.UserId, "RoleCreated", $"role={role.Id}");
        return role;
    });

    public RoleBinding RenameRole(string token, RenameRoleRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Text(request.Name, "역할 이름");
        var roles = request.ExpectedAssigned ? s.Roles : s.UnassignedRoles;
        var role = RequireCurrentRole(roles, request.ExpectedRole);
        // Display-only edits preserve the binding version and accepted execution snapshots.
        var renamed = role with { Name = request.Name.Trim(), IsDefault = false };
        roles[roles.IndexOf(role)] = renamed;
        _host.Audit(s, session.Info.UserId, "RoleRenamed", $"role={role.Id}; device={role.DeviceId}; v={role.Version}");
        return renamed;
    });

    public bool DeleteRole(string token, DeleteRoleRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        var roles = request.ExpectedAssigned ? s.Roles : s.UnassignedRoles;
        var role = RequireCurrentRole(roles, request.ExpectedRole);
        Require(!request.ExpectedAssigned && s.Roles.All(r => r.Id != role.Id),
            "role_assigned", "장비에 배정된 역할은 삭제할 수 없습니다. 먼저 역할 배정을 해제하세요.");
        Require(!s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(step => step.Role?.Id == role.Id)),
            "role_in_use", "이 역할을 사용하는 작업이 진행 중입니다. 작업·교대에서 완료를 확인하거나 취소한 뒤 삭제하세요.");
        var references = s.Scenarios.Where(d => d.Steps.Any(step => step.RoleId == role.Id)).ToArray();
        Require(references.Length == 0, "role_referenced",
            $"역할을 사용하는 시나리오가 있습니다: {string.Join(", ", references.Select(d => d.Name))}. 해당 단계의 역할을 변경하거나 시나리오를 삭제한 뒤 다시 시도하세요.");
        s.DeletedRoleVersions[role.Id] = Math.Max(role.Version, s.DeletedRoleVersions.GetValueOrDefault(role.Id));
        s.RemovedRoleIds.Add(role.Id);
        roles.Remove(role);
        _host.Audit(s, session.Info.UserId, "RoleDeleted", $"role={role.Id}; device={role.DeviceId}; v={role.Version}");
        return true;
    });

    private static RoleBinding RequireCurrentRole(List<RoleBinding> roles, RoleBinding? expected)
    {
        var current = roles.SingleOrDefault(r => r.Id == expected?.Id);
        Require(current is not null && current == expected, "version_conflict",
            "역할 이름 또는 배정이 변경되었거나 삭제되었습니다. 역할을 다시 불러오세요.");
        return current!;
    }
}
