using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class DeviceExecutionService : IDeviceScenarioOperations
{
    private readonly IDeviceStateAccess _host;
    private readonly DeviceDriverRegistry _drivers;
    internal DeviceExecutionService(IDeviceStateAccess host, DeviceDriverRegistry drivers)
    { _host = host; _drivers = drivers; }
    internal DeviceModel[] Models => _drivers.Models;
    public DeviceConfig SaveDevice(string token, DeviceRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Require(request.Id != Guid.Empty && request.PcId != Guid.Empty, "target_required", "PC ID와 장비 ID를 명시하세요.", 400);
        Text(request.PcName, "대상 PC 이름"); Text(request.Name, "장비 이름"); Text(request.ConnectionId, "연결 ID");
        var model = _drivers.Model(request.ModelId);
        var old = s.Devices.SingleOrDefault(d => d.Id == request.Id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "장비 설정을 다시 조회하세요.");
        var device = new DeviceConfig(request.Id, request.PcId, request.PcName.Trim(), request.Name.Trim(),
            request.ConnectionId.Trim(), request.ModelId, (old?.Version ?? 0) + 1, request.Enabled, request.Fault, request.LatencyMs)
        {
            DriverId = request.DriverId ?? model.DriverId,
            Connection = JsonDefaults.Copy(request.Connection ?? old?.Connection ?? new DeviceConnection()),
            DriverOptions = JsonDefaults.Copy(request.DriverOptions ?? old?.DriverOptions ?? new Dictionary<string, string>())
        };
        _drivers.Resolve(device);
        var executionChanged = old is null || !old.HasSameExecutionSettings(device);
        device = device with { ExecutionVersion = old is null ? device.Version :
            executionChanged ? old.ExecutionVersion + 1 : old.ExecutionVersion };
        s.Devices.RemoveAll(d => d.Id == device.Id); s.Devices.Add(device);
        if (executionChanged)
            s.DeviceStates[device.Id] = new DeviceState { Connection = "장비 설정 저장 / 새 상태 조회 필요" };
        // Replacing a model in-place must satisfy the same role requirements as assigning a new device.
        if (device.Enabled)
            foreach (var role in s.Roles.Where(r => r.DeviceId == device.Id))
                foreach (var step in s.Scenarios.SelectMany(x => x.Steps).Where(x => x.RoleId == role.Id))
                    Resolve(s, _host.User(s, session), step);
        _host.Audit(s, session.Info.UserId, "DeviceSaved", $"pc={device.PcId}; device={device.Id}; v={device.Version}");
        return device;
    });
    public RoleBinding SaveRole(string token, RoleRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Text(request.Id, "역할 ID");
        Require(s.Devices.Any(d => d.Id == request.DeviceId && d.Enabled), "target_missing", "활성 장비를 선택하세요.", 400);
        var old = s.Roles.SingleOrDefault(r => r.Id == request.Id.Trim());
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "역할 설정을 다시 조회하세요.");
        var role = new RoleBinding(request.Id.Trim(), request.DeviceId,
            checked(Math.Max(old?.Version ?? 0, s.DeletedRoleVersions.GetValueOrDefault(request.Id.Trim())) + 1));
        // Existing scenarios declare the required capabilities of this role.
        foreach (var step in s.Scenarios.SelectMany(x => x.Steps).Where(x => x.RoleId == role.Id))
            Resolve(s, _host.User(s, session), step with { RoleId = role.Id }, role);
        s.Roles.RemoveAll(r => r.Id == role.Id); s.Roles.Add(role);
        _host.Audit(s, session.Info.UserId, "RoleAssigned", $"role={role.Id}; device={role.DeviceId}; v={role.Version}");
        return role;
    });
    public bool UnassignRole(string token, UnassignRoleRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Text(request.Id, "역할 ID");
        Require(request.DeviceId != Guid.Empty && request.ExpectedVersion > 0, "invalid_role", "해제할 장비와 역할 배정을 확인하세요.", 400);
        var role = s.Roles.SingleOrDefault(r => r.Id == request.Id.Trim());
        Require(role is not null && role.DeviceId == request.DeviceId && role.Version == request.ExpectedVersion,
            "version_conflict", "역할 배정이 변경되었거나 해제되었습니다. 다시 조회하세요.");
        Require(!s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(step => step.Role?.Id == role!.Id)),
            "role_in_use", "이 역할을 사용하는 작업이 진행 중입니다. 작업·교대에서 완료를 확인하거나 취소한 뒤 해제하세요.");
        s.DeletedRoleVersions[role!.Id] = role.Version;
        s.Roles.Remove(role);
        _host.Audit(s, session.Info.UserId, "RoleUnassigned", $"role={role.Id}; device={role.DeviceId}; v={role.Version}");
        return true;
    });
    public StepSnapshot Resolve(StateContext context, AccountView user, ScenarioStep step, RoleBinding? overrideRole = null)
    {
        var s = _host.For(context);
        ValidateStep(step);
        Require(step.Kind != ScenarioStepKind.DisplayLayout, "invalid_step", "장비 단계만 실행할 수 있습니다.", 400);
        Require(step.LayoutId is null, "invalid_step", "장비 단계에 배치를 지정할 수 없습니다.", 400);
        var role = overrideRole ?? s.Roles.SingleOrDefault(r => r.Id == step.RoleId);
        Require(role is not null, "role_missing", $"역할을 찾을 수 없습니다: {step.RoleId}", 400);
        var device = s.Devices.SingleOrDefault(d => d.Id == role!.DeviceId);
        Require(device is not null && device.Enabled, "target_missing", "대상이 없거나 비활성화되었습니다.", 400);
        Require(CanControl(user, device!.Id), "target_forbidden", "대상 장비 제어 권한이 없습니다.", 403);
        _drivers.Resolve(device);
        var model = _drivers.Model(device.ModelId);
        var capability = model.Capabilities.SingleOrDefault(c => c.Operation == step.Operation);
        Require(capability is not null, "unsupported", "이 모델은 해당 기능을 지원하지 않습니다.", 400);
        Require(step.Value >= capability!.Minimum && step.Value <= capability.Maximum, "value_range",
            $"허용 범위: {capability.Minimum}~{capability.Maximum} {capability.Unit}", 400);
        Require((step.ConditionOperation is null) == (step.ConditionValue is null), "invalid_condition", "확인 조건의 동작과 값을 함께 지정하세요.", 400);
        if (step.Kind == ScenarioStepKind.WaitUntil)
            Require(capability.CanRead && step.Operation != DeviceOperation.Stop && step.ConditionOperation is null,
                "invalid_condition", "조건 대기는 읽을 기능·기대값만 지정합니다. STOP은 상태 조건이 아닙니다.", 400);
        if (step.ConditionOperation is { } condition)
        {
            var c = model.Capabilities.SingleOrDefault(x => x.Operation == condition);
            Require(c is { CanRead: true } && step.ConditionValue >= c.Minimum && step.ConditionValue <= c.Maximum,
                "invalid_condition", "조회 가능한 기능과 상태 확인 조건을 지정하세요.", 400);
        }
        return new(role!, JsonDefaults.Copy(device), step.Operation, step.Value, capability.Unit, step.DelayBeforeMs, step.TimeoutMs,
            step.OnFailure, step.ConditionOperation, step.ConditionValue) { Kind = step.Kind, ModelDefinition = model };
    }
    public async Task<DeviceState> ReconcileAsync(string token, ReconcileRequest request, CancellationToken ct = default)
    {
        DeviceConfig target;
        using (_host.Open())
        {
            _host.Healthy(); _host.CheckConnections();
            var session = _host.Owner(_host.Current, token, request.Generation);
            target = _host.Current.Devices.SingleOrDefault(x => x.Id == request.DeviceId)
                ?? throw new DomainException("target_missing", "대상 장비가 없습니다.", 404);
            Require(CanControl(_host.User(_host.Current, session), target.Id), "target_forbidden", "대상 제어 권한이 없습니다.", 403);
            Require(!_host.Current.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == target.Id)),
                "device_busy", "대상 작업의 전송·중단 처리가 끝난 후 조회·대조하세요.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        DriverReading reading;
        try { reading = await _drivers.Resolve(target).ReadAsync(JsonDefaults.Copy(target), timeout.Token); }
        catch (OperationCanceledException) { throw new DomainException("read_timeout", "상태 조회 제한시간 초과"); }
        return _host.Change(s =>
        {
            var session = _host.Owner(s, token, request.Generation);
            Require(CanControl(_host.User(s, session), target.Id) && s.Devices.Any(x => x.MatchesExecutionTarget(target)) &&
                !s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == target.Id)),
                "reconcile_changed", "대상/권한/작업이 변경되었습니다. 다시 대조하세요.");
            Require(ValidReading(target, reading), "read_failed", "상태 조회 실패 또는 관측 증거 없음");
            var state = s.DeviceStates[target.Id];
            RecordValues(state, target, reading.Values, reading.Evidence, replace: true);
            state.Connection = ConnectedLabel(target);
            state.LastResult = "상태 대조 완료 / 과거 불확실 명령의 성공 판정 아님";
            s.UncertainDevices.Remove(target.Id);
            _host.Audit(s, session.Info.UserId, "DeviceStateReconciled", $"device={target.Id}; 과거 작업 상태는 보존");
            return state;
        });
    }
    public string? RevalidateTarget(StateContext context, Job job, StepSnapshot step)
    {
        var s = _host.For(context);
        var user = _host.FindAccount(s, job.Snapshot.RequestedBy);
        if (step.Target is not { } target || step.Role is not { } binding) return "장비 대상 누락";
        if (user is null || !CanControl(user, target.Id)) return "원 요청자 계정/대상 권한 회수";
        var device = s.Devices.SingleOrDefault(d => d.Id == target.Id);
        if (device is null || !device.Enabled || !device.MatchesExecutionTarget(target)) return "장비 대상/설정 버전 변경";
        try
        {
            _drivers.Resolve(device);
            var model = _drivers.Model(device.ModelId);
            if (step.ModelDefinition is { } admitted && !DeviceDriverRegistry.SameDefinition(admitted, model))
                return "드라이버 기능 정의 변경";
            if (step.ModelDefinition is null && !model.IsSimulation) return "기존 가상 snapshot의 실장비 실행 차단";
            var capability = model.Capabilities.SingleOrDefault(c => c.Operation == step.Operation);
            if (capability is null || step.Unit != capability.Unit || step.Value < capability.Minimum || step.Value > capability.Maximum ||
                (step.Kind == ScenarioStepKind.WaitUntil && !capability.CanRead)) return "지원 기능 변경";
            if (step.ConditionOperation is { } condition && !model.Capabilities.Any(c => c.Operation == condition && c.CanRead &&
                step.ConditionValue >= c.Minimum && step.ConditionValue <= c.Maximum)) return "조회 기능 변경";
        }
        catch (DomainException) { return "드라이버/통신 설정을 사용할 수 없음"; }
        var role = s.Roles.SingleOrDefault(r => r.Id == binding.Id);
        if (role is null || role.Version != binding.Version || role.DeviceId != target.Id) return "역할 배정 변경";
        if (s.UncertainDevices.Contains(device.Id) && (step.Kind != ScenarioStepKind.DeviceCommand || step.Operation != DeviceOperation.Stop)) return "장비 상태 대조 필요";
        return null;
    }
    public Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken ct) =>
        _drivers.Resolve(step.Target!).ExecuteAsync(JsonDefaults.Copy(step), ct);
    public Task<DriverReading> ReadAsync(DeviceConfig target, CancellationToken ct) =>
        _drivers.Resolve(target).ReadAsync(JsonDefaults.Copy(target), ct);
    public DriverResult NormalizeResult(StepSnapshot snapshot, DriverResult result)
    {
        if (snapshot.Target is { } resultTarget &&
            ((result.Status == StepStatus.Simulated && (!_drivers.Model(resultTarget.ModelId).IsSimulation || result.Evidence != DeviceEvidence.Simulation)) ||
             (result.Status == StepStatus.Observed && (_drivers.Model(resultTarget.ModelId).IsSimulation || result.Evidence != DeviceEvidence.Observed)) ||
             (result.Status == StepStatus.Observed && result.Values is null) ||
             (result.Status is StepStatus.Simulated or StepStatus.Observed && result.Values is not null && !ValidValues(resultTarget, result.Values))))
            result = new(StepStatus.Unknown, "드라이버 결과와 상태 증거 불일치 / 대조 필요");
        return result;
    }
    public void RecordResult(StateContext context, StepSnapshot snapshot, DriverResult result)
    {
        var next = _host.For(context);
        if (snapshot.Kind == ScenarioStepKind.DeviceCommand && snapshot.Target is { } target &&
            next.Devices.Any(d => d.MatchesExecutionTarget(target)) && next.DeviceStates.TryGetValue(target.Id, out var device))
        {
            device.LastResult = result.Detail;
            device.Connection = result.Status is StepStatus.Simulated or StepStatus.Observed or StepStatus.Acknowledged
                ? ConnectedLabel(target) : result.Status == StepStatus.Sent ? "전송 완료 / 장비 응답 미확인" : "장비 오류/대조 필요";
            if (result.Values is not null && result.Status is StepStatus.Simulated or StepStatus.Observed)
                RecordValues(device, target, result.Values, result.Evidence);
            if (result.Status == StepStatus.Unknown && !next.UncertainDevices.Contains(target.Id)) next.UncertainDevices.Add(target.Id);
        }
    }
    public void RecordConditionReading(StateContext context, StepSnapshot step, DriverReading reading)
    {
        var state = _host.For(context);
        var device = state.DeviceStates[step.Target!.Id];
        RecordValues(device, step.Target, reading.Values, reading.Evidence);
        device.Connection = ConnectedLabel(step.Target); device.LastResult = "조건 대기 중 최신 상태 조회";
    }
    public void RecordDispatchIntent(StateContext context, StepSnapshot step) =>
        _host.For(context).DeviceStates[step.Target!.Id].Desired[step.Operation] = step.Value;
    public void MarkUncertain(StateContext context, IEnumerable<Guid> ids)
    {
        var state = _host.For(context);
        foreach (var id in ids)
            if (!state.UncertainDevices.Contains(id)) state.UncertainDevices.Add(id);
    }
    public void RecoverInterrupted(StateContext context, StepSnapshot step, string message)
    {
        var state = _host.For(context);
        if (step.Target is { } target && state.Devices.Any(d => d.MatchesExecutionTarget(target)) &&
            state.DeviceStates.TryGetValue(target.Id, out var device))
        {
            device.Connection = "호스트 중단 / 상태 대조 필요"; device.LastResult = message;
            MarkUncertain(context, [target.Id]);
        }
    }
}
