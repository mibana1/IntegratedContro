using IntegratedContro.Core;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private void FinishStep(HostState state, Job job, int index, DriverResult result, DeviceCommandResult? evidence = null)
    {
        var snapshot = job.Snapshot.Steps[index]; var run = job.Steps[index];
        run.Status = result.Status; run.Result = result.Detail; run.FinishedAt = Now;
        run.Evidence = evidence ?? new(result.Outcome, result.Confirmation, result.Detail, Now);
        var cancelled = job.CancelRequestedAt is not null;
        var stop = cancelled || result.Status is StepStatus.Unknown or StepStatus.Skipped ||
            (result.Status is not (StepStatus.Simulated or StepStatus.Succeeded) && snapshot.OnFailure == FailurePolicy.Stop);
        if (stop)
        {
            foreach (var pending in job.Steps.Where(x => x.Status is StepStatus.Pending or StepStatus.Waiting))
            { pending.Status = StepStatus.Skipped; pending.Result = "후속 단계 차단"; pending.FinishedAt = Now; }
            job.Status = result.Status == StepStatus.Unknown ? JobStatus.NeedsReview : cancelled ? JobStatus.Cancelled : JobStatus.Interrupted;
            job.Result = cancelled ? "미전송 부분 취소 완료 / 전송된 결과 보존. 물리 정지·롤백 아님." : result.Detail;
        }
        else
        {
            var nextIndex = job.Steps.FindIndex(x => x.Status is StepStatus.Pending or StepStatus.Waiting);
            job.Status = nextIndex < 0 ? JobStatus.Completed : JobStatus.Running;
            job.Result = nextIndex < 0
                ? (job.Steps.Any(x => x.Status == StepStatus.Failed) ? "순차 실행 종료 / 실패 단계 포함" :
                    job.Steps.All(x => x.Status == StepStatus.Simulated) ? "가상 순차 실행 완료 / 실측 아님" : "순차 실행 종료 / 단계별 확인 근거를 확인하세요.")
                : $"단계 {index + 1}/{job.Steps.Count} 종료";
            if (nextIndex >= 0) job.ReadyAt = Now.AddMilliseconds(job.Snapshot.Steps[nextIndex].DelayBeforeMs);
        }
        Audit(state, null, "DispatchResult", $"job={job.Id}; step={index}; result={run.Status}");
    }
    private bool CompleteLayoutSteps(HostState state)
    {
        var changed = false;
        foreach (var job in state.Jobs.Where(j => j.Active))
        {
            var index = job.Steps.FindIndex(s => s.Status == StepStatus.Dispatching);
            if (index < 0 || job.Snapshot.Steps[index].Kind != ScenarioStepKind.ShowLayout) continue;
            var receipt = state.HiperwallEdits.Single(r => r.Request.RequestId == job.Snapshot.Steps[index].DisplayReceiptId);
            var invalid = Revalidate(state, job, job.Snapshot.Steps[index]);
            if (Now >= job.Steps[index].WaitDeadline || invalid is not null)
                foreach (var pending in receipt.Steps.Where(s => s.State == HiperwallSendState.Pending))
                { pending.State = HiperwallSendState.Rejected; pending.Message = invalid ?? "배치 표시 제한시간 초과 / 미전송"; changed = true; }
            if (receipt.Active) continue;
            var unknown = receipt.Steps.Any(s => s.State == HiperwallSendState.Unknown);
            var rejected = receipt.Steps.Any(s => s.State == HiperwallSendState.Rejected);
            var blocked = invalid is not null || receipt.Steps.Any(s => s.BlocksFollowingSteps);
            var expired = Now >= job.Steps[index].WaitDeadline;
            var detail = $"{job.Snapshot.Steps[index].SavedLayout!.Name} · {receipt.Summary}";
            if (expired && rejected) detail += " · 표시 제한시간 초과";
            if (blocked) detail += " · 전송 전 대상·권한 검증 실패 / 후속 단계 차단";
            job.Steps[index].HiperwallResults = JsonDefaults.Copy(receipt.Steps);
            if (invalid is not null) detail += " · " + invalid;
            var result = new DriverResult(unknown ? StepStatus.Unknown : rejected ? StepStatus.Failed : StepStatus.Succeeded, detail)
            {
                Outcome = unknown ? CommandOutcome.Unknown : expired && rejected ? CommandOutcome.TimedOut : rejected ? CommandOutcome.Failed : CommandOutcome.Succeeded,
                Confirmation = unknown ? ConfirmationLevel.None : receipt.Steps.Any(s => s.State == HiperwallSendState.Acknowledged)
                    ? ConfirmationLevel.ProtocolAcknowledged : ConfirmationLevel.None
            };
            // Authority/configuration changes always stop the scenario, regardless of its ordinary failure policy.
            if (blocked)
                foreach (var pending in job.Steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Waiting))
                { pending.Status = StepStatus.Skipped; pending.Result = invalid ?? "배치 전송 전 대상 검증 실패"; pending.FinishedAt = Now; }
            FinishStep(state, job, index, result);
            if (blocked)
            {
                if (job.Status == JobStatus.Completed) job.Status = JobStatus.Interrupted;
                job.Result = detail;
            }
            changed = true;
        }
        return changed;
    }
    private async Task PollConditionAsync(Guid jobId, int index, StepSnapshot snapshot, CancellationToken stopping)
    {
        DateTimeOffset deadline;
        lock (_gate)
        {
            var run = FindJob(_state, jobId).Steps[index];
            deadline = run.WaitDeadline!.Value;
        }
        DriverReading? reading = null;
        if (Now < deadline && !stopping.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(3000, (deadline - Now).TotalMilliseconds))));
            try { reading = await _driver.ReadAsync(snapshot.Target!, timeout.Token); }
            catch (Exception e) when (e is not OutOfMemoryException) { /* Retry read only, within the persisted deadline. */ }
        }
        lock (_gate)
        {
            if (_storageFailed) return;
            var next = JsonDefaults.Copy(_state); var job = FindJob(next, jobId); var run = job.Steps[index];
            if (run.Status != StepStatus.Waiting || job.CancelRequestedAt is not null) return;
            var invalid = Revalidate(next, job, snapshot);
            if (_stopping || stopping.IsCancellationRequested || invalid is not null)
            {
                foreach (var pending in job.Steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Waiting))
                { pending.Status = StepStatus.Skipped; pending.Result = invalid ?? "호스트 종료: 조건 대기 자동 재개 금지"; pending.FinishedAt = Now; }
                job.Status = JobStatus.Interrupted; job.Result = run.Result; Persist(next); return;
            }
            run.ObservationsChecked++;
            var matched = reading is not null && reading.Observations is not null && reading.Values is not null &&
                Now < deadline && ConditionSatisfied(snapshot, reading);
            if (matched)
            {
                var actual = reading!.Confirmation == ConfirmationLevel.Observed;
                var result = new DriverResult(actual ? StepStatus.Succeeded : StepStatus.Simulated,
                    $"조건 충족: {snapshot.Operation}={snapshot.Value} {snapshot.Unit} · 조회 {run.ObservationsChecked}회")
                    { Confirmation = reading.Confirmation };
                var evidence = new DeviceCommandResult(CommandOutcome.Succeeded, reading.Confirmation, result.Detail, Now)
                    { Observations = actual ? ValidObservations(snapshot.Target!, reading.Observations!) : [] };
                if (actual)
                    foreach (var pair in evidence.Observations) next.DeviceStates[snapshot.Target!.Id].Observed[pair.Key] = pair.Value;
                else
                    foreach (var pair in reading.Values!) next.DeviceStates[snapshot.Target!.Id].Simulated[pair.Key] = new(pair.Value, Now);
                FinishStep(next, job, index, result, evidence);
            }
            else if (Now >= deadline)
                FinishStep(next, job, index, new(StepStatus.Failed, $"조건 대기 제한시간 초과 · 조회 {run.ObservationsChecked}회 / 명령 전송 없음")
                    { Outcome = CommandOutcome.TimedOut });
            else
            {
                run.Result = $"조건 대기 중: {snapshot.Operation}={snapshot.Value} · 조회 {run.ObservationsChecked}회 · 마감 {deadline.ToLocalTime():HH:mm:ss}";
                job.Result = run.Result;
                var nextRead = Now.AddMilliseconds(snapshot.PollIntervalMs); job.ReadyAt = nextRead < deadline ? nextRead : deadline;
            }
            Persist(next);
        }
    }
}
