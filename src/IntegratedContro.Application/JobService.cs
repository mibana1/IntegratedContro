using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public Job Submit(string token, SubmitRequest request) => Change(s =>
    {
        var session = Authenticate(token);
        Require(request.RequestId != Guid.Empty, "request_id_required", "요청 ID가 필요합니다.", 400);
        var fingerprint = Digest(JsonSerializer.Serialize(request, JsonDefaults.Options));
        var existing = s.Jobs.SingleOrDefault(j => j.Snapshot.RequestId == request.RequestId);
        if (existing is not null)
        {
            Require(existing.Snapshot.RequestedBy == session.Info.UserId &&
                existing.Snapshot.SessionId == session.Info.Id && existing.RequestFingerprint == fingerprint,
                "request_id_conflict", "같은 요청 ID를 다른 내용이나 세션에서 사용할 수 없습니다.");
            return existing; // A committed request is discoverable even after release or fencing.
        }
        Owner(s, token, request.Generation);
        Require(request.ExpiresAfterSeconds is >= 1 and <= 604800, "invalid_expiry", "만료는 1~604800초입니다.", 400);
        ScenarioDefinition? definition = null;
        ScenarioStep[] steps;
        if (request.ScenarioId is { } scenarioId)
        {
            definition = s.Scenarios.SingleOrDefault(x => x.Id == scenarioId);
            Require(definition is not null, "scenario_missing", "시나리오를 찾을 수 없습니다.", 404);
            steps = definition!.Steps;
        }
        else
        {
            Text(request.RoleId, "역할 ID");
            steps = [new(request.RoleId!, request.Operation, request.Value, request.DelayBeforeMs, request.TimeoutMs)];
        }
        var snapshots = steps.Select(step => Resolve(s, User(s, session), step)).ToArray();
        ValidateCardPower(s, request, snapshots);
        var targets = snapshots.Where(x => x.Target is not null).Select(x => x.Target!.Id).ToHashSet();
        if (snapshots.Any(x => x.Kind == ScenarioStepKind.DisplayLayout))
        {
            RequireHiperwallScenarioAvailable(s);
            Require(!s.HiperwallEdits.Any(e => e.Active) && !s.HiperwallDisplays.Any(d => d.Targets.Any(t => t.OpenState is HiperwallSendState.Pending or HiperwallSendState.Sending)),
                "hiperwall_busy", "진행 중인 Hiperwall 전송을 확인한 뒤 시나리오를 시작하세요.");
        }
        Require(!s.Jobs.Any(j => j.Active && j.IsLightBatch && j.Snapshot.Steps.Any(x => x.Target is not null && targets.Contains(x.Target.Id))),
            "lighting_batch_busy", "일괄 조명 작업이 남아 있습니다. 작업 탭에서 결과를 확인하거나 미전송 부분을 취소하세요.");
        var isStop = definition is null && request.Operation == DeviceOperation.Stop;
        var conflicts = s.Jobs.Where(j => HoldsReservations(j) &&
            j.Snapshot.Steps.Any(x => x.Target is not null && targets.Contains(x.Target.Id)) &&
            (definition is not null || j.Kind == JobKind.Scenario)).ToArray();
        if (isStop)
        {
            foreach (var job in conflicts)
            {
                Require(CanControlJob(User(s, session), job),
                    "target_forbidden", "연결된 시나리오의 중단 권한이 없습니다.", 403);
                StopJob(s, job, session, "STOP 우선 요청: 시나리오 후속 단계 차단");
            }
        }
        else
        {
            Require(conflicts.Length == 0, "device_reserved",
                "장비가 예약되어 있습니다. 작업을 확인하고 '시나리오 중단 후 수동 전환'을 명시적으로 선택하세요.");
            Require(!targets.Overlaps(s.UncertainDevices), "device_uncertain", "불확실 장비의 상태를 먼저 대조하세요.");
        }
        var snapshot = new ExecutionSnapshot(s.SiteId, ExecutionMode(snapshots), request.RequestId, session.Info.UserId,
            session.Info.UserName, session.Info.Id, session.Info.PcId, session.Info.PcName, request.Generation,
            Now, Now.AddSeconds(request.ExpiresAfterSeconds), definition?.Id, definition?.Version,
            definition?.Name ?? $"{request.RoleId}: {request.Operation}={request.Value}", snapshots);
        var jobNew = new Job { RequestFingerprint = fingerprint, Snapshot = snapshot,
            Kind = definition is null ? JobKind.Manual : JobKind.Scenario,
            Steps = snapshots.Select(_ => new StepRun()).ToList(), ReadyAt = Now.AddMilliseconds(snapshots[0].DelayBeforeMs) };
        s.Jobs.Add(jobNew);
        Audit(s, session.Info.UserId, "JobAccepted", $"job={jobNew.Id}; request={request.RequestId}; generation={request.Generation}");
        return jobNew;
    });
    public Job Cancel(string token, JobActionRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation);
        var job = FindJob(s, request.JobId);
        Require(CanControlJob(User(s, session), job),
            "target_forbidden", "작업 대상 전체에 대한 제어 권한이 필요합니다.", 403);
        StopJob(s, job, session, "선택 취소");
        return job;
    });
    public Job BeginManualSwitch(string token, JobActionRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation);
        var job = FindJob(s, request.JobId);
        Require(job.Kind == JobKind.Scenario, "scenario_required", "전환할 시나리오를 선택하세요.");
        Require(CanControlJob(User(s, session), job),
            "target_forbidden", "시나리오 대상 전체 제어 권한이 필요합니다.", 403);
        StopJob(s, job, session, "시나리오 중단 후 수동 전환 요청");
        // Reconciliation is a separate explicit operation. Never enqueue the earlier conflicting click.
        foreach (var id in job.Snapshot.Steps.Where(x => x.Target is not null).Select(x => x.Target!.Id).Distinct())
            if (!s.UncertainDevices.Contains(id)) s.UncertainDevices.Add(id);
        job.Result += " / 장비는 상태 대조, Hiperwall은 남은 전송·불확실 표시를 확인한 뒤 새 수동 조작을 선택하세요.";
        return job;
    });
    private static Job FindJob(HostState s, Guid id)
    {
        var job = s.Jobs.SingleOrDefault(x => x.Id == id);
        Require(job is not null, "job_missing", "작업을 찾을 수 없습니다.", 404);
        return job!;
    }
    private void StopJob(HostState s, Job job, Session session, string reason)
    {
        if (!job.Active && job.Status != JobStatus.NeedsReview) return;
        job.CancelledBy ??= session.Info.UserId;
        job.CancellerName ??= session.Info.UserName;
        job.CancelRequestedAt ??= Now;
        StopScenarioPendingDisplays(s, job, "시나리오 중단: 미전송 표시 차단");
        foreach (var (step, index) in job.Steps.Select((value, index) => (value, index)))
        {
            var display = s.HiperwallDisplays.SingleOrDefault(d => d.ScenarioJobId == job.Id && d.ScenarioStepIndex == index);
            if (step.Status == StepStatus.Waiting && display?.Targets.Any(t => t.OpenState == HiperwallSendState.Sending) == true)
                continue; // Keep reservations until an in-flight open has a recorded outcome.
            if (step.Status is StepStatus.Pending or StepStatus.Waiting)
            {
                step.Status = display?.Targets.Any(t => t.OpenState == HiperwallSendState.Unknown) == true ? StepStatus.Unknown : StepStatus.Skipped;
                step.Result = "미전송·대기 부분 취소 / 이미 열린 표시는 정리 일정 유지"; step.FinishedAt = Now;
            }
        }
        job.Status = job.Steps.Any(x => x.Status is StepStatus.Dispatching or StepStatus.Waiting) ? JobStatus.StopRequested :
            job.Steps.Any(x => x.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Cancelled;
        job.ReadyAt = Now;
        job.Result = job.Status == JobStatus.StopRequested ? "취소 요청 / 이미 전송된 단계 결과 대기. 물리 정지·롤백 아님."
            : "미전송·대기 부분 취소 완료. 이미 열린 표시와 전송 결과는 유지합니다.";
        Audit(s, session.Info.UserId, "JobCancellation", $"job={job.Id}; {reason}; status={job.Status}");
    }
    public async Task<DeviceState> ReconcileAsync(string token, ReconcileRequest request, CancellationToken ct = default)
    {
        DeviceConfig target;
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var session = Owner(_state, token, request.Generation);
            target = _state.Devices.SingleOrDefault(x => x.Id == request.DeviceId)
                ?? throw new DomainException("target_missing", "대상 장비가 없습니다.", 404);
            Require(CanControl(User(_state, session), target.Id), "target_forbidden", "대상 제어 권한이 없습니다.", 403);
            Require(!_state.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == target.Id)),
                "device_busy", "대상 작업의 전송·중단 처리가 끝난 후 조회·대조하세요.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        DriverReading reading;
        try { reading = await _drivers.Resolve(target).ReadAsync(JsonDefaults.Copy(target), timeout.Token); }
        catch (OperationCanceledException) { throw new DomainException("read_timeout", "상태 조회 제한시간 초과"); }
        return Change(s =>
        {
            var session = Owner(s, token, request.Generation);
            Require(CanControl(User(s, session), target.Id) && s.Devices.Any(x => x.MatchesExecutionTarget(target)) &&
                !s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == target.Id)),
                "reconcile_changed", "대상/권한/작업이 변경되었습니다. 다시 대조하세요.");
            Require(ValidReading(target, reading), "read_failed", "상태 조회 실패 또는 관측 증거 없음");
            var state = s.DeviceStates[target.Id];
            RecordValues(state, target, reading.Values, reading.Evidence, replace: true);
            state.Connection = ConnectedLabel(target);
            state.LastResult = "상태 대조 완료 / 과거 불확실 명령의 성공 판정 아님";
            s.UncertainDevices.Remove(target.Id);
            Audit(s, session.Info.UserId, "DeviceStateReconciled", $"device={target.Id}; 과거 작업 상태는 보존");
            return state;
        });
    }
    private string? Revalidate(HostState s, Job job, StepSnapshot step)
    {
        if (job.Snapshot.SiteId != s.SiteId || job.Snapshot.Mode is not ("Virtual" or "Mixed" or "Physical")) return "현장/실행 모드 불일치";
        if (Now >= job.Snapshot.ExpiresAt) return "작업 만료";
        if (job.Snapshot.ScenarioId is { } scenarioId &&
            !s.Scenarios.Any(x => x.Id == scenarioId && x.Version == job.Snapshot.ScenarioVersion)) return "시나리오 정의 변경";
        var user = s.Accounts.SingleOrDefault(a => a.Id == job.Snapshot.RequestedBy);
        if (step.Kind == ScenarioStepKind.DisplayLayout)
        {
            if (user is null || !HiperwallPermission(user)) return "원 요청자 Hiperwall 권한 회수";
            if (step.Display is not { } display || s.Hiperwall?.Version != display.Layout.ConfigurationVersion ||
                s.Hiperwall.Endpoint != display.Endpoint || _hiperwall is not IHiperwallWriter) return "Hiperwall 연결 설정 변경";
            return null; // Saved layout changes cannot mutate an admitted scenario snapshot.
        }
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
}
