using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class ScenarioService
{
    public Job Submit(string token, SubmitRequest request) => _host.Change(s =>
    {
        var session = _host.Authenticate(token);
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
        _host.Owner(s, token, request.Generation);
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
        var snapshots = steps.Select(step => Resolve(s, _host.User(s, session), step)).ToArray();
        _devices.ValidateCardPower(s, request, snapshots);
        var targets = snapshots.Where(x => x.Target is not null).Select(x => x.Target!.Id).ToHashSet();
        if (snapshots.Any(x => x.Kind == ScenarioStepKind.DisplayLayout))
        {
            _displays.ValidateScenarioAdmission(s);
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
                Require(CanControlJob(_host.User(s, session), job),
                    "target_forbidden", "연결된 시나리오의 중단 권한이 없습니다.", 403);
                _jobs.StopJob(s, job.Id, session, "STOP 우선 요청: 시나리오 후속 단계 차단");
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
            _host.Now, _host.Now.AddSeconds(request.ExpiresAfterSeconds), definition?.Id, definition?.Version,
            definition?.Name ?? $"{request.RoleId}: {request.Operation}={request.Value}", snapshots);
        var jobNew = new Job { RequestFingerprint = fingerprint, Snapshot = snapshot,
            Kind = definition is null ? JobKind.Manual : JobKind.Scenario,
            Steps = snapshots.Select(_ => new StepRun()).ToList(), ReadyAt = _host.Now.AddMilliseconds(snapshots[0].DelayBeforeMs) };
        s.Jobs.Add(jobNew);
        _host.Audit(s, session.Info.UserId, "JobAccepted", $"job={jobNew.Id}; request={request.RequestId}; generation={request.Generation}");
        return jobNew;
    });
    public Job Cancel(string token, JobActionRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation);
        var job = FindJob(s.Jobs, request.JobId);
        Require(CanControlJob(_host.User(s, session), job),
            "target_forbidden", "작업 대상 전체에 대한 제어 권한이 필요합니다.", 403);
        _jobs.StopJob(s, job.Id, session, "선택 취소");
        return job;
    });
    public Job BeginManualSwitch(string token, JobActionRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation);
        var job = FindJob(s.Jobs, request.JobId);
        Require(job.Kind == JobKind.Scenario, "scenario_required", "전환할 시나리오를 선택하세요.");
        Require(CanControlJob(_host.User(s, session), job),
            "target_forbidden", "시나리오 대상 전체 제어 권한이 필요합니다.", 403);
        _jobs.StopJob(s, job.Id, session, "시나리오 중단 후 수동 전환 요청");
        // Reconciliation is a separate explicit operation. Never enqueue the earlier conflicting click.
        _devices.MarkUncertain(s, job.Snapshot.Steps.Where(x => x.Target is not null).Select(x => x.Target!.Id).Distinct());
        job.Result += " / 장비는 상태 대조, Hiperwall은 남은 전송·불확실 표시를 확인한 뒤 새 수동 조작을 선택하세요.";
        return job;
    });
}
