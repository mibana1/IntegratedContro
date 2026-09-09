using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public AccountView CreateAccount(string token, CreateAccountRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        AccountName(request.Name); Password(request.Password);
        Require(Enum.IsDefined(request.Role), "invalid_role", "계정 역할을 확인하세요.", 400);
        Require(!s.Accounts.Any(a => a.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)),
            "duplicate_account", "이미 등록된 계정입니다.");
        var ids = request.DeviceIds ?? [];
        Require(ids.All(id => s.Devices.Any(d => d.Id == id)), "invalid_scope", "권한 대상 장비를 확인하세요.", 400);
        var account = new Account { Name = request.Name.Trim(), PasswordHash = _passwords.Hash(request.Password),
            Role = request.Role, AllDevices = request.AllDevices, DeviceIds = ids.Distinct().ToList() };
        s.Accounts.Add(account);
        Audit(s, session.Info.UserId, "AccountCreated", $"account={account.Id}; role={account.Role}");
        return new AccountView(account.Id, account.Name, account.Role, account.Enabled, account.AllDevices, account.DeviceIds.ToArray());
    });
    public bool UpdateAccount(string token, UpdateAccountRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        var account = s.Accounts.SingleOrDefault(a => a.Id == request.AccountId);
        Require(account is not null, "account_missing", "계정을 찾을 수 없습니다.", 404);
        Require(request.Enabled || account!.Role != AccountRole.Administrator ||
            s.Accounts.Any(a => a.Id != account.Id && a.Enabled && a.Role == AccountRole.Administrator),
            "last_admin", "마지막 관리자는 비활성화할 수 없습니다.");
        Require(request.DeviceIds.All(id => s.Devices.Any(d => d.Id == id)), "invalid_scope", "권한 대상 장비를 확인하세요.", 400);
        account!.Enabled = request.Enabled; account.AllDevices = request.AllDevices;
        account.DeviceIds = request.DeviceIds.Distinct().ToList();
        if (!account.Enabled && s.Lease.UserId == account.Id && s.Lease.Mode == LeaseMode.Held)
            Fence(s, "사용 계정 권한 회수");
        Audit(s, session.Info.UserId, "AccountPermissionsChanged", $"account={account.Id}");
        return true;
    });
    public DeviceConfig SaveDevice(string token, DeviceRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        Require(request.Id != Guid.Empty && request.PcId != Guid.Empty, "target_required", "PC ID와 장비 ID를 명시하세요.", 400);
        Text(request.PcName, "대상 PC 이름"); Text(request.Name, "장비 이름"); Text(request.ConnectionId, "연결 ID");
        Require(_driver.Models.Any(m => m.Id == request.ModelId), "unsupported_model", "등록된 가상 모델을 선택하세요.", 400);
        Require(Enum.IsDefined(request.Fault) && request.LatencyMs is >= 0 and <= 30000,
            "invalid_device", "가상 지연은 0~30000ms입니다.", 400);
        var old = s.Devices.SingleOrDefault(d => d.Id == request.Id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "장비 설정을 다시 조회하세요.");
        var device = new DeviceConfig(request.Id, request.PcId, request.PcName.Trim(), request.Name.Trim(),
            request.ConnectionId.Trim(), request.ModelId, (old?.Version ?? 0) + 1, request.Enabled, request.Fault, request.LatencyMs);
        s.Devices.RemoveAll(d => d.Id == device.Id); s.Devices.Add(device);
        s.DeviceStates[device.Id] = new DeviceState { Connection = "가상 설정 저장 / 새 상태 조회 필요" };
        Audit(s, session.Info.UserId, "VirtualDeviceSaved", $"pc={device.PcId}; device={device.Id}; v={device.Version}");
        return device;
    });
    public RoleBinding SaveRole(string token, RoleRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        Text(request.Id, "역할 ID");
        Require(s.Devices.Any(d => d.Id == request.DeviceId && d.Enabled), "target_missing", "활성 장비를 선택하세요.", 400);
        var old = s.Roles.SingleOrDefault(r => r.Id == request.Id.Trim());
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "역할 설정을 다시 조회하세요.");
        var role = new RoleBinding(request.Id.Trim(), request.DeviceId, (old?.Version ?? 0) + 1);
        // Existing scenarios declare the required capabilities of this role.
        foreach (var step in s.Scenarios.SelectMany(x => x.Steps).Where(x => x.RoleId == role.Id))
            Resolve(s, User(s, session), step with { RoleId = role.Id }, role);
        s.Roles.RemoveAll(r => r.Id == role.Id); s.Roles.Add(role);
        Audit(s, session.Info.UserId, "RoleAssigned", $"role={role.Id}; device={role.DeviceId}; v={role.Version}");
        return role;
    });
    public ScenarioDefinition SaveScenario(string token, ScenarioRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        Text(request.Name, "시나리오 이름");
        Require(request.Id != Guid.Empty && request.Steps is { Length: >= 1 and <= 100 },
            "invalid_scenario", "시나리오는 1~100단계로 구성하세요.", 400);
        var steps = request.Steps.ToArray();
        foreach (var step in steps) Resolve(s, User(s, session), step);
        var old = s.Scenarios.SingleOrDefault(x => x.Id == request.Id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "시나리오 설정을 다시 조회하세요.");
        var definition = new ScenarioDefinition(request.Id, request.Name.Trim(), (old?.Version ?? 0) + 1, steps);
        s.Scenarios.RemoveAll(x => x.Id == definition.Id); s.Scenarios.Add(definition);
        Audit(s, session.Info.UserId, "ScenarioSaved", $"scenario={definition.Id}; v={definition.Version}");
        return definition;
    });
    private StepSnapshot Resolve(HostState s, Account user, ScenarioStep step, RoleBinding? overrideRole = null)
    {
        var role = overrideRole ?? s.Roles.SingleOrDefault(r => r.Id == step.RoleId);
        Require(role is not null, "role_missing", $"역할을 찾을 수 없습니다: {step.RoleId}", 400);
        var device = s.Devices.SingleOrDefault(d => d.Id == role!.DeviceId);
        Require(device is not null && device.Enabled, "target_missing", "대상이 없거나 비활성화되었습니다.", 400);
        Require(CanControl(user, device!.Id), "target_forbidden", "대상 장비 제어 권한이 없습니다.", 403);
        var capability = _driver.Models.Single(m => m.Id == device.ModelId).Capabilities.SingleOrDefault(c => c.Operation == step.Operation);
        Require(capability is not null, "unsupported", "이 모델은 해당 기능을 지원하지 않습니다.", 400);
        Require(step.Value >= capability!.Minimum && step.Value <= capability.Maximum, "value_range",
            $"허용 범위: {capability.Minimum}~{capability.Maximum} {capability.Unit}", 400);
        Require(step.DelayBeforeMs is >= 0 and <= 3600000 && step.TimeoutMs is >= 100 and <= 30000 &&
            Enum.IsDefined(step.OnFailure), "invalid_timing", "대기는 0~3600000ms, 제한시간은 100~30000ms입니다.", 400);
        Require((step.ConditionOperation is null) == (step.ConditionValue is null), "invalid_condition", "확인 조건의 동작과 값을 함께 지정하세요.", 400);
        if (step.ConditionOperation is { } condition)
        {
            var c = _driver.Models.Single(m => m.Id == device.ModelId).Capabilities.SingleOrDefault(x => x.Operation == condition);
            Require(c is not null && step.ConditionValue >= c.Minimum && step.ConditionValue <= c.Maximum,
                "invalid_condition", "지원되는 가상 상태 확인 조건을 지정하세요.", 400);
        }
        return new(role!, device, step.Operation, step.Value, capability.Unit, step.DelayBeforeMs, step.TimeoutMs,
            step.OnFailure, step.ConditionOperation, step.ConditionValue);
    }
}
