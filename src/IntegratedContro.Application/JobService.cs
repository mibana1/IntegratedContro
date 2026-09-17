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
        return _devices.RevalidateTarget(s, job, step);
    }
}
