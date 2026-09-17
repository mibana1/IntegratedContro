using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class HiperwallService
{
    public StepSnapshot ResolveDisplay(HostState s, Account user, ScenarioStep step)
    {
        Require(string.IsNullOrEmpty(step.RoleId) && step.ConditionOperation is null && step.ConditionValue is null &&
        step.LayoutId is not null, "invalid_step", "배치 표시에는 저장 배치만 지정하세요.", 400);
        Require(HiperwallPermission(user), "hiperwall_scope", "배치 표시에는 전체 장비 제어 권한이 필요합니다.", 403);
        Require(_hiperwall is IHiperwallWriter && _credentials is not null && s.Hiperwall is not null,
        "hiperwall_unavailable", "Hiperwall 연결 설정이 필요합니다.");
        var layout = s.HiperwallLayouts.SingleOrDefault(l => l.Id == step.LayoutId);
        Require(layout is not null && layout.ConfigurationVersion == s.Hiperwall!.Version, "layout_changed",
        "저장 배치가 없거나 연결 설정이 변경되었습니다. 배치를 검토·저장하세요.");
        ValidatePlacements(layout!.Placements);
        Require(layout.Duration.IsValid, "invalid_duration", "배치 표시 시간을 확인하세요.", 400);
        return new(null, null, default, 0, "", step.DelayBeforeMs, step.TimeoutMs, step.OnFailure, null, null)
        { Kind = step.Kind, Display = new(JsonDefaults.Copy(layout), s.Hiperwall!.Endpoint, Guid.NewGuid()) };
    }
    public string? RevalidateDisplay(HostState s, Job job, StepSnapshot step)
    {
        var invalid = RevalidateJob(s, job, _host.Now);
        if (invalid is not null) return invalid;
        var user = s.Accounts.SingleOrDefault(a => a.Id == job.Snapshot.RequestedBy);
        if (user is null || !HiperwallPermission(user)) return "원 요청자 Hiperwall 권한 회수";
        if (step.Display is not { } display || s.Hiperwall?.Version != display.Layout.ConfigurationVersion ||
        s.Hiperwall.Endpoint != display.Endpoint || _hiperwall is not IHiperwallWriter) return "Hiperwall 연결 설정 변경";
        return null; // Saved layout changes cannot mutate an admitted scenario snapshot.
    }
    private static void RequireHiperwallScenarioAvailable(HostState state)
    {
        Require(!state.Jobs.Any(j => j.Active && j.Kind == JobKind.Scenario && j.Snapshot.Steps.Any(s => s.Kind == ScenarioStepKind.DisplayLayout)),
            "hiperwall_reserved", "시나리오가 Hiperwall을 예약하고 있습니다. 작업·교대에서 시나리오 중단 후 수동 전환을 선택하세요.");
        Require(!state.HiperwallDisplays.Any(d => d.ScenarioJobId is not null && d.Targets.Any(t => t.Outstanding && t.OpenState is HiperwallSendState.Sending or HiperwallSendState.Unknown)),
            "hiperwall_uncertain", "이전 시나리오의 불확실한 표시를 먼저 목록 대조·정리하세요.");
    }
    private bool ScenarioAllowsDisplay(HiperwallDisplayJob display)
    {
        if (display.ScenarioJobId is not { } id) return true;
        var parent = _host.State.Jobs.SingleOrDefault(j => j.Id == id);
        return parent is { Active: true, CancelRequestedAt: null } && display.ScenarioStepIndex is { } index &&
            index >= 0 && index < parent.Steps.Count && parent.Steps[index].Status == StepStatus.Waiting &&
            _host.Now < parent.Steps[index].DeadlineAt &&
            RevalidateDisplay(_host.State, parent, parent.Snapshot.Steps[index]) is null &&
            !display.Targets.Any(t => t.OpenState is HiperwallSendState.Rejected or HiperwallSendState.Unknown);
    }
    public void ValidateScenarioAdmission(HostState state)
    {
        RequireHiperwallScenarioAvailable(state);
        Require(!state.HiperwallEdits.Any(e => e.Active) && !state.HiperwallDisplays.Any(d => d.Targets.Any(t => t.OpenState is HiperwallSendState.Pending or HiperwallSendState.Sending)),
            "hiperwall_busy", "진행 중인 Hiperwall 전송을 확인한 뒤 시나리오를 시작하세요.");
    }
    public async Task<DriverResult?> PollScenarioDisplayAsync(Guid id, int index, StepSnapshot step, CancellationToken ct)
    {
        HiperwallConfiguration config;
        lock (_host.Gate)
        {
            var parent = FindJob(_host.State, id);
            var existing = _host.State.HiperwallDisplays.SingleOrDefault(d => d.ScenarioJobId == id && d.ScenarioStepIndex == index);
            if (existing is not null) return ReadScenarioDisplayResult(parent, index, existing);
            if (parent.CancelRequestedAt is not null) return new(StepStatus.Skipped, "시나리오 표시 접수 전 취소");
            if (_host.Now >= parent.Steps[index].DeadlineAt) return new(StepStatus.Failed, "표시 단계 제한시간 초과");
            config = _host.State.Hiperwall!;
        }
        var snapshot = step.Display!;
        var commands = snapshot.Layout.Placements.Select((p, i) => new HiperwallWireCommand(HiperwallEditAction.Open,
            $"integrated-{snapshot.RequestId:N}-{i}", p.Selector, p.ContentValue, p.ZoneId, p.Layout, p.Volume, p.Muted)).ToArray();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(Math.Min(config.TimeoutMs, step.TimeoutMs));
            var secret = config.CredentialId is { } key ? _credentials!.Read(key) : null;
            HiperwallReading reading;
            try { reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token).ConfigureAwait(false); }
            finally { secret = null; }
            RequireWritableInventory(reading);
            foreach (var command in commands) ValidateDisplayContent(command, reading);
            lock (_host.Gate)
            {
                if (_host.Stopping || _host.StorageFailed || ct.IsCancellationRequested) return null;
                var next = JsonDefaults.Copy(_host.State); var parent = FindJob(next, id);
                if (parent.CancelRequestedAt is not null || parent.Steps[index].Status != StepStatus.Waiting)
                    return new(StepStatus.Skipped, "시나리오 표시 접수 전 취소");
                var invalid = RevalidateDisplay(next, parent, step);
                if (invalid is not null) return new(StepStatus.Skipped, invalid);
                if (_host.Now >= parent.Steps[index].DeadlineAt) return new(StepStatus.Failed, "표시 단계 제한시간 초과");
                Require(next.HiperwallDisplays.Count(d => d.Outstanding) < 100, "display_limit", "남은 표시 작업을 먼저 정리하세요.");
                var user = next.Accounts.Single(a => a.Id == parent.Snapshot.RequestedBy);
                var display = new HiperwallDisplayJob {
                    Request = new(snapshot.RequestId, parent.Snapshot.LeaseGeneration, snapshot.Layout.ConfigurationVersion, snapshot.Layout.Id, snapshot.Layout.Version),
                    Requester = new(parent.Snapshot.SessionId, user.Id, parent.Snapshot.RequesterName, user.Role, parent.Snapshot.ClientPcId, parent.Snapshot.ClientPcName),
                    Endpoint = snapshot.Endpoint, Name = snapshot.Layout.Name, Duration = snapshot.Layout.Duration, AcceptedAt = _host.Now,
                    CloseAt = snapshot.Layout.Duration.EffectiveSeconds is { } seconds ? _host.Now.AddSeconds(seconds) : null,
                    ScenarioJobId = id, ScenarioStepIndex = index,
                    Targets = commands.Select(c => new HiperwallDisplayTarget { Command = c, NextAttemptAt = _host.Now }).ToList()
                };
                next.HiperwallDisplays.Add(display);
                parent.ReadyAt = _host.Now.AddMilliseconds(100); parent.Steps[index].Result = "저장 배치 표시 접수 / Controller 응답 대기";
                _host.Audit(next, user.Id, "HiperwallDisplayAccepted", $"request={snapshot.RequestId}; targets={commands.Length}; scenarioJob={id}");
                _host.Persist(next); return null;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (ct.IsCancellationRequested || _host.Stopping) return null;
            return e is DomainException { Code: "hiperwall_content_missing" or "hiperwall_zone_missing" or "hiperwall_instance_exists" }
                ? new(StepStatus.Skipped, e.Message)
                : new(StepStatus.Failed, e is DomainException error ? error.Message : "배치 표시 전 Controller 조회 실패 / 명령 미전송");
        }
    }
    // Called under the same authority lock as display dispatch/cancellation.
    private DriverResult? ReadScenarioDisplayResult(Job parent, int index, HiperwallDisplayJob display)
    {
        if (display.Targets.Any(t => t.OpenState == HiperwallSendState.Unknown))
            return new(StepStatus.Unknown, "배치 표시 결과 불확실 / 후속 단계 차단, 표시 목록 대조 필요");
        var sending = display.Targets.Any(t => t.OpenState == HiperwallSendState.Sending);
        if (!sending && parent.CancelRequestedAt is not null)
            return new(StepStatus.Skipped, "시나리오 표시 미전송 부분 취소 / 이미 열린 표시·정리 일정 유지");
        if (_host.Now >= parent.Steps[index].DeadlineAt)
            return new(sending ? StepStatus.Unknown : StepStatus.Failed, "표시 단계 제한시간 초과 / 열린 표시 기록과 정리 일정 유지");
        if (!sending && display.Targets.Any(t => t.OpenState == HiperwallSendState.Rejected))
            return new(StepStatus.Failed, "배치 표시 일부 또는 전체 거부 / 개별 표시 결과 확인");
        if (display.Targets.All(t => t.OpenState == HiperwallSendState.Acknowledged))
            return new(StepStatus.Acknowledged, "배치 표시 Controller 응답 확인 / 실제 영상벽 표시 검증 아님");
        var next = JsonDefaults.Copy(_host.State); var job = FindJob(next, parent.Id);
        job.ReadyAt = _host.Now.AddMilliseconds(100);
        job.Steps[index].Result = display.Summary;
        job.Result = $"단계 {index + 1}/{job.Steps.Count} · 표시 응답 대기";
        _host.Persist(next); return null;
    }
    internal void RecoverEdits(HostState next)
    {
        foreach (var edit in next.HiperwallEdits.Where(r => r.Active))
            foreach (var step in edit.Steps.Where(s => s.State is HiperwallSendState.Pending or HiperwallSendState.Sending))
            { step.State = step.State == HiperwallSendState.Sending ? HiperwallSendState.Unknown : HiperwallSendState.Rejected;
              step.Message = "호스트 재시작: 자동 재전송하지 않습니다. Controller 목록과 대조하세요."; }
    }
}
