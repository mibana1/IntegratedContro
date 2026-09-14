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
        Require(request.ScenarioId is null || request.SavedLayoutId is null, "invalid_request", "시나리오와 수동 배치 표시를 함께 요청할 수 없습니다.", 400);
        ScenarioDefinition? definition = null;
        ScenarioStep[] steps;
        if (request.ScenarioId is { } scenarioId)
        {
            definition = s.Scenarios.SingleOrDefault(x => x.Id == scenarioId);
            Require(definition is not null, "scenario_missing", "시나리오를 찾을 수 없습니다.", 404);
            steps = definition!.Steps;
        }
        else if (request.SavedLayoutId is { } layoutId)
            steps = [new("", DeviceOperation.Power, 0, request.DelayBeforeMs, request.TimeoutMs)
                { Kind = ScenarioStepKind.ShowLayout, SavedLayoutId = layoutId }];
        else
        {
            Text(request.RoleId, "역할 ID");
            steps = [new(request.RoleId!, request.Operation, request.Value, request.DelayBeforeMs, request.TimeoutMs)];
        }
        var snapshots = steps.Select(step => Resolve(s, User(s, session), step)).ToArray();
        if (request.SavedLayoutId is not null)
            Require(request.SavedLayoutVersion == snapshots[0].SavedLayout!.Version, "version_conflict", "저장 배치가 변경되었습니다. 다시 조회하고 표시하세요.");
        ValidateCardPower(s, request, snapshots);
        var targets = DeviceTargets(snapshots).ToHashSet();
        var displays = snapshots.Any(x => x.Kind == ScenarioStepKind.ShowLayout);
        if (displays)
            Require(!s.Jobs.Any(j => j.Active && UsesHiperwall(j)) && !s.HiperwallEdits.Any(r => r.Active),
                "hiperwall_reserved", "Hiperwall 작업이 남아 있습니다. 작업 탭에서 결과를 확인하거나 시나리오를 명시적으로 중단하세요.");
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
                Require(job.Snapshot.Steps.All(x => CanControlStep(User(s, session), x)),
                    "target_forbidden", "연결된 시나리오의 중단 권한이 없습니다.", 403);
                StopJob(s, job, session, "STOP 우선 요청: 시나리오 후속 단계 차단");
            }
        }
        else
        {
            Require(conflicts.Length == 0, "device_reserved",
                "장비가 예약되어 있습니다. 작업을 확인하고 '시나리오 중단 후 수동 전환'을 명시적으로 선택하세요.");
            Require(!targets.Overlaps(s.UncertainDevices), "device_uncertain", "불확실 장비의 가상 상태를 먼저 대조하세요.");
        }
        var snapshot = new ExecutionSnapshot(s.SiteId, ExecutionMode(snapshots), request.RequestId, session.Info.UserId,
            session.Info.UserName, session.Info.Id, session.Info.PcId, session.Info.PcName, request.Generation,
            Now, Now.AddSeconds(request.ExpiresAfterSeconds), definition?.Id, definition?.Version,
            definition?.Name ?? (displays ? $"배치 표시: {snapshots[0].SavedLayout!.Name}" : $"{request.RoleId}: {request.Operation}={request.Value}"), snapshots);
        var jobNew = new Job { RequestFingerprint = fingerprint, Snapshot = snapshot,
            Kind = definition is not null ? JobKind.Scenario : displays ? JobKind.LayoutDisplay : JobKind.Manual,
            Steps = snapshots.Select(_ => new StepRun()).ToList(), ReadyAt = Now.AddMilliseconds(snapshots[0].DelayBeforeMs) };
        s.Jobs.Add(jobNew);
        Audit(s, session.Info.UserId, "JobAccepted", $"job={jobNew.Id}; request={request.RequestId}; generation={request.Generation}");
        return jobNew;
    });
    public Job Cancel(string token, JobActionRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation);
        var job = FindJob(s, request.JobId);
        Require(job.Snapshot.Steps.All(step => CanControlStep(User(s, session), step)),
            "target_forbidden", "작업 대상 전체에 대한 제어 권한이 필요합니다.", 403);
        StopJob(s, job, session, "선택 취소");
        return job;
    });
    public Job BeginManualSwitch(string token, JobActionRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation);
        var job = FindJob(s, request.JobId);
        Require(job.Kind == JobKind.Scenario, "scenario_required", "전환할 시나리오를 선택하세요.");
        Require(job.Snapshot.Steps.All(step => CanControlStep(User(s, session), step)),
            "target_forbidden", "시나리오 대상 전체 제어 권한이 필요합니다.", 403);
        StopJob(s, job, session, "시나리오 중단 후 수동 전환 요청");
        // Reconciliation is a separate explicit operation. Never enqueue the earlier conflicting click.
        foreach (var id in DeviceTargets(job.Snapshot.Steps).Distinct())
            if (!s.UncertainDevices.Contains(id)) s.UncertainDevices.Add(id);
        job.Result += " / 전환 대상의 가상 상태 조회·대조 후 새 수동 명령을 선택하세요.";
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
        foreach (var receipt in s.HiperwallEdits.Where(r => r.ParentJobId == job.Id))
            foreach (var pending in receipt.Steps.Where(x => x.State == HiperwallSendState.Pending))
            { pending.State = HiperwallSendState.Rejected; pending.Message = "원 작업 취소: 미전송 배치 항목 차단"; }
        foreach (var step in job.Steps.Where(x => x.Status is StepStatus.Pending or StepStatus.Waiting))
        { step.Status = StepStatus.Skipped; step.Result = "미전송 부분 취소"; step.FinishedAt = Now; }
        job.Status = job.Steps.Any(x => x.Status == StepStatus.Dispatching) ? JobStatus.StopRequested :
            job.Steps.Any(x => x.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Cancelled;
        job.Result = job.Status == JobStatus.StopRequested
            ? "취소 요청 / 이미 전송된 단계 결과 대기. 물리 정지·롤백 아님."
            : "미전송 부분 취소 완료. 이미 전송된 결과 보존; 물리 정지·롤백 아님.";
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
        try { reading = await _driver.ReadAsync(target, timeout.Token); }
        catch (OperationCanceledException) { throw new DomainException("read_timeout", "대상 상태 조회 제한시간 초과"); }
        return Change(s =>
        {
            var session = Owner(s, token, request.Generation);
            Require(CanControl(User(s, session), target.Id) && s.Devices.Any(x => x.MatchesExecutionTarget(target)) &&
                !s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == target.Id)),
                "reconcile_changed", "대상/권한/작업이 변경되었습니다. 다시 대조하세요.");
            Require(reading.Available, "read_failed", reading.Detail);
            var state = s.DeviceStates[target.Id];
            if (reading.Confirmation == ConfirmationLevel.Simulated)
                state.Simulated = reading.Values.ToDictionary(x => x.Key, x => new StateValue(x.Value, Now));
            else
            {
                var observed = ValidObservations(target, reading.Observations);
                var readable = _driver.Models.Single(m => m.Id == target.ModelId).Capabilities.Where(c => c.CanRead).ToArray();
                Require(reading.Confirmation == ConfirmationLevel.Observed && readable.Length > 0 &&
                    readable.All(c => observed.TryGetValue(c.Operation, out var value) && value.IsFresh(Now)),
                    "read_unconfirmed", "최신 실제 관측값이 부족합니다. ACK만으로 상태 대조를 완료할 수 없습니다.");
                state.Observed = observed;
            }
            state.ConnectionStatus = DeviceConnectionStatus.Connected;
            state.Connection = reading.Confirmation == ConfirmationLevel.Simulated ? "가상 연결됨" : "장비 상태 관측";
            state.LastResult = "상태 대조 완료 / 과거 불확실 명령의 성공 판정 아님";
            s.UncertainDevices.Remove(target.Id);
            Audit(s, session.Info.UserId, reading.Confirmation == ConfirmationLevel.Simulated ? "VirtualStateReconciled" : "ObservedStateReconciled",
                $"device={target.Id}; 과거 작업 상태는 보존");
            return state;
        });
    }
    private string? Revalidate(HostState s, Job job, StepSnapshot step)
    {
        if (job.Snapshot.SiteId != s.SiteId || job.Snapshot.Mode != ExecutionMode(job.Snapshot.Steps)) return "현장/실행 모드 불일치";
        if (Now >= job.Snapshot.ExpiresAt) return "작업 만료";
        if (job.Snapshot.ScenarioId is { } currentScenario && !s.Scenarios.Any(x => x.Id == currentScenario && x.Version == job.Snapshot.ScenarioVersion))
            return "시나리오 정의 변경";
        if (step.Kind == ScenarioStepKind.ShowLayout) return ValidateLayoutDispatch(s, job, step);
        if (step.Target is null || step.Role is null) return "장비 대상 snapshot 오류";
        var user = s.Accounts.SingleOrDefault(a => a.Id == job.Snapshot.RequestedBy);
        if (user is null || !CanControl(user, step.Target.Id)) return "원 요청자 계정/대상 권한 회수";
        var device = s.Devices.SingleOrDefault(d => d.Id == step.Target.Id);
        if (device is null || !device.Enabled || !device.MatchesExecutionTarget(step.Target)) return "장비 대상/설정 버전 변경";
        var currentCapability = _driver.Models.SingleOrDefault(m => m.Id == device.ModelId)?.Capabilities.SingleOrDefault(c => c.Operation == step.Operation);
        if (currentCapability is null || (step.Capability is not null && step.Capability != currentCapability))
            return "드라이버 기능/실행 제약 변경";
        if (step.ConditionOperation is { } condition)
        {
            var currentCondition = _driver.Models.Single(m => m.Id == device.ModelId).Capabilities.SingleOrDefault(c => c.Operation == condition);
            if (currentCondition is not { CanRead: true } || (step.ConditionCapability is not null && step.ConditionCapability != currentCondition))
                return "드라이버 상태 확인 조건/관측 제약 변경";
        }
        var role = s.Roles.SingleOrDefault(r => r.Id == step.Role.Id);
        if (role is null || role.Version != step.Role.Version || role.DeviceId != step.Target.Id) return "역할 배정 변경";
        if (job.Snapshot.ScenarioId is { } scenarioId &&
            !s.Scenarios.Any(x => x.Id == scenarioId && x.Version == job.Snapshot.ScenarioVersion)) return "시나리오 정의 변경";
        if (s.UncertainDevices.Contains(device.Id) && step.Operation != DeviceOperation.Stop) return "장비 상태 대조 필요";
        return null;
    }
}
