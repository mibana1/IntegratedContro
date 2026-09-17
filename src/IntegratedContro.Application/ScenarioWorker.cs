using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private async Task<DriverResult?> PollScenarioConditionAsync(Guid id, int index, StepSnapshot step, CancellationToken ct)
    {
        DateTimeOffset deadline;
        lock (_gate)
        {
            var job = FindJob(_state, id);
            if (job.CancelRequestedAt is not null) return new(StepStatus.Skipped, "조건 대기 취소");
            deadline = job.Steps[index].DeadlineAt!.Value;
            if (Now >= deadline) return new(StepStatus.Failed, "조건 대기 제한시간 초과 / 장비 명령은 전송하지 않았습니다.");
        }
        DriverReading? reading = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(3000, (deadline - Now).TotalMilliseconds))));
        try { reading = await _drivers.Resolve(step.Target!).ReadAsync(JsonDefaults.Copy(step.Target!), timeout.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* A failed read is not a failed physical command. */ }
        lock (_gate)
        {
            if (_storageFailed || _stopping || ct.IsCancellationRequested) return null;
            var next = JsonDefaults.Copy(_state); var job = FindJob(next, id);
            if (job.CancelRequestedAt is not null || job.Steps[index].Status != StepStatus.Waiting)
                return new(StepStatus.Skipped, "조건 대기 취소");
            var invalid = Revalidate(next, job, step);
            if (invalid is not null) return new(StepStatus.Skipped, invalid);
            if (Now >= deadline) return new(StepStatus.Failed, "조건 대기 제한시간 초과 / 후속 단계 실패 정책 적용");
            if (reading is not null && ValidReading(step.Target!, reading))
            {
                var device = next.DeviceStates[step.Target!.Id];
                RecordValues(device, step.Target!, reading.Values, reading.Evidence);
                device.Connection = ConnectedLabel(step.Target!); device.LastResult = "조건 대기 중 최신 상태 조회";
                if (reading.Values.TryGetValue(step.Operation, out var value) && value == step.Value)
                { Persist(next); return new(StepStatus.ConditionMet, $"{(reading.Evidence == DeviceEvidence.Simulation ? "가상 상태" : "장비 관측")} 조건 충족: {step.Operation} = {value}"); }
            }
            job.Steps[index].Result = reading is not null && ValidReading(step.Target!, reading) ? $"조건 대기: {step.Operation} = {step.Value} / 아직 충족하지 않음" :
                "조건 상태 조회 실패 / 제한시간 안에서 재확인";
            job.ReadyAt = Now.AddMilliseconds(500); if (job.ReadyAt > deadline) job.ReadyAt = deadline;
            job.Result = $"단계 {index + 1}/{job.Steps.Count} · {job.Steps[index].Result}";
            Persist(next); return null;
        }
    }
    private async Task<DriverResult?> PollScenarioDisplayAsync(Guid id, int index, StepSnapshot step, CancellationToken ct)
    {
        HiperwallConfiguration config;
        lock (_gate)
        {
            var parent = FindJob(_state, id);
            var existing = _state.HiperwallDisplays.SingleOrDefault(d => d.ScenarioJobId == id && d.ScenarioStepIndex == index);
            if (existing is not null) return ReadScenarioDisplayResult(parent, index, existing);
            if (parent.CancelRequestedAt is not null) return new(StepStatus.Skipped, "시나리오 표시 접수 전 취소");
            if (Now >= parent.Steps[index].DeadlineAt) return new(StepStatus.Failed, "표시 단계 제한시간 초과");
            config = _state.Hiperwall!;
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
            lock (_gate)
            {
                if (_stopping || _storageFailed || ct.IsCancellationRequested) return null;
                var next = JsonDefaults.Copy(_state); var parent = FindJob(next, id);
                if (parent.CancelRequestedAt is not null || parent.Steps[index].Status != StepStatus.Waiting)
                    return new(StepStatus.Skipped, "시나리오 표시 접수 전 취소");
                var invalid = Revalidate(next, parent, step);
                if (invalid is not null) return new(StepStatus.Skipped, invalid);
                if (Now >= parent.Steps[index].DeadlineAt) return new(StepStatus.Failed, "표시 단계 제한시간 초과");
                Require(next.HiperwallDisplays.Count(d => d.Outstanding) < 100, "display_limit", "남은 표시 작업을 먼저 정리하세요.");
                var user = next.Accounts.Single(a => a.Id == parent.Snapshot.RequestedBy);
                var display = new HiperwallDisplayJob {
                    Request = new(snapshot.RequestId, parent.Snapshot.LeaseGeneration, snapshot.Layout.ConfigurationVersion, snapshot.Layout.Id, snapshot.Layout.Version),
                    Requester = new(parent.Snapshot.SessionId, user.Id, parent.Snapshot.RequesterName, user.Role, parent.Snapshot.ClientPcId, parent.Snapshot.ClientPcName),
                    Endpoint = snapshot.Endpoint, Name = snapshot.Layout.Name, Duration = snapshot.Layout.Duration, AcceptedAt = Now,
                    CloseAt = snapshot.Layout.Duration.EffectiveSeconds is { } seconds ? Now.AddSeconds(seconds) : null,
                    ScenarioJobId = id, ScenarioStepIndex = index,
                    Targets = commands.Select(c => new HiperwallDisplayTarget { Command = c, NextAttemptAt = Now }).ToList()
                };
                next.HiperwallDisplays.Add(display);
                parent.ReadyAt = Now.AddMilliseconds(100); parent.Steps[index].Result = "저장 배치 표시 접수 / Controller 응답 대기";
                Audit(next, user.Id, "HiperwallDisplayAccepted", $"request={snapshot.RequestId}; targets={commands.Length}; scenarioJob={id}");
                Persist(next); return null;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (ct.IsCancellationRequested || _stopping) return null;
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
        if (Now >= parent.Steps[index].DeadlineAt)
            return new(sending ? StepStatus.Unknown : StepStatus.Failed, "표시 단계 제한시간 초과 / 열린 표시 기록과 정리 일정 유지");
        if (!sending && display.Targets.Any(t => t.OpenState == HiperwallSendState.Rejected))
            return new(StepStatus.Failed, "배치 표시 일부 또는 전체 거부 / 개별 표시 결과 확인");
        if (display.Targets.All(t => t.OpenState == HiperwallSendState.Acknowledged))
            return new(StepStatus.Acknowledged, "배치 표시 Controller 응답 확인 / 실제 영상벽 표시 검증 아님");
        var next = JsonDefaults.Copy(_state); var job = FindJob(next, parent.Id);
        job.ReadyAt = Now.AddMilliseconds(100);
        job.Steps[index].Result = display.Summary;
        job.Result = $"단계 {index + 1}/{job.Steps.Count} · 표시 응답 대기";
        Persist(next); return null;
    }
}
