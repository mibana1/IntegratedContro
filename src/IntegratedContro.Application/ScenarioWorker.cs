using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class ScenarioService
{
    private async Task<DriverResult?> PollScenarioConditionAsync(Guid id, int index, StepSnapshot step, CancellationToken ct)
    {
        DateTimeOffset deadline;
        lock (_host.Gate)
        {
            var job = FindJob(_host.State, id);
            if (job.CancelRequestedAt is not null) return new(StepStatus.Skipped, "조건 대기 취소");
            deadline = job.Steps[index].DeadlineAt!.Value;
            if (_host.Now >= deadline) return new(StepStatus.Failed, "조건 대기 제한시간 초과 / 장비 명령은 전송하지 않았습니다.");
        }
        DriverReading? reading = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(3000, (deadline - _host.Now).TotalMilliseconds))));
        try { reading = await _devices.ReadAsync(step.Target!, timeout.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* A failed read is not a failed physical command. */ }
        lock (_host.Gate)
        {
            if (_host.StorageFailed || _host.Stopping || ct.IsCancellationRequested) return null;
            var next = JsonDefaults.Copy(_host.State); var job = FindJob(next, id);
            if (job.CancelRequestedAt is not null || job.Steps[index].Status != StepStatus.Waiting)
                return new(StepStatus.Skipped, "조건 대기 취소");
            var invalid = Revalidate(next, job, step);
            if (invalid is not null) return new(StepStatus.Skipped, invalid);
            if (_host.Now >= deadline) return new(StepStatus.Failed, "조건 대기 제한시간 초과 / 후속 단계 실패 정책 적용");
            if (reading is not null && _devices.ValidReading(step.Target!, reading))
            {
                _devices.RecordConditionReading(next, step, reading);
                if (reading.Values.TryGetValue(step.Operation, out var value) && value == step.Value)
                { _host.Persist(next); return new(StepStatus.ConditionMet, $"{(reading.Evidence == DeviceEvidence.Simulation ? "가상 상태" : "장비 관측")} 조건 충족: {step.Operation} = {value}"); }
            }
            job.Steps[index].Result = reading is not null && _devices.ValidReading(step.Target!, reading) ? $"조건 대기: {step.Operation} = {step.Value} / 아직 충족하지 않음" :
                "조건 상태 조회 실패 / 제한시간 안에서 재확인";
            job.ReadyAt = _host.Now.AddMilliseconds(500); if (job.ReadyAt > deadline) job.ReadyAt = deadline;
            job.Result = $"단계 {index + 1}/{job.Steps.Count} · {job.Steps[index].Result}";
            _host.Persist(next); return null;
        }
    }
}
