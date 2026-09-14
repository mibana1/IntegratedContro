using IntegratedContro.Core;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private void RecoverStartup()
    {
        var next = JsonDefaults.Copy(_state);
        if (next.Lease.Mode != LeaseMode.Free) Fence(next, "호스트 재시작: 이전 인증 세션 무효");
        foreach (var edit in next.HiperwallEdits.Where(r => r.Active))
            foreach (var step in edit.Steps.Where(s => s.State is HiperwallSendState.Pending or HiperwallSendState.Sending))
            { step.State = step.State == HiperwallSendState.Sending ? HiperwallSendState.Unknown : HiperwallSendState.Rejected;
              step.Message = "호스트 재시작: 자동 재전송하지 않습니다. Controller 목록과 대조하세요."; }
        foreach (var job in next.Jobs.Where(j => j.Active))
        {
            foreach (var (run, index) in job.Steps.Select((x, i) => (x, i)))
            {
                if (run.Status != StepStatus.Dispatching) continue;
                if (job.Snapshot.Steps[index].Kind == ScenarioStepKind.ShowLayout)
                {
                    var receipt = next.HiperwallEdits.SingleOrDefault(r => r.Request.RequestId == job.Snapshot.Steps[index].DisplayReceiptId);
                    var unknown = receipt is null || receipt.Steps.Any(s => s.State == HiperwallSendState.Unknown);
                    var ack = receipt?.Steps.Any(s => s.State == HiperwallSendState.Acknowledged) == true;
                    var allAcknowledged = receipt?.Steps.All(s => s.State == HiperwallSendState.Acknowledged) == true;
                    run.HiperwallResults = receipt is null ? [] : JsonDefaults.Copy(receipt.Steps);
                    run.Status = unknown ? StepStatus.Unknown : allAcknowledged ? StepStatus.Succeeded : ack ? StepStatus.Failed : StepStatus.Skipped;
                    run.Result = unknown ? "호스트 중단: 배치 표시 결과 불확실 / 자동 재전송 금지" :
                        ack ? "호스트 중단: 확인된 배치 항목 결과 보존 / 나머지 자동 실행 금지" : "호스트 중단: 배치 미전송 / 자동 실행 금지";
                    run.Evidence = new(unknown ? CommandOutcome.Unknown : allAcknowledged ? CommandOutcome.Succeeded : ack ? CommandOutcome.Failed : CommandOutcome.Cancelled,
                        ack ? ConfirmationLevel.ProtocolAcknowledged : ConfirmationLevel.None, run.Result, Now);
                    run.FinishedAt = Now; continue;
                }
                run.Status = StepStatus.Unknown; run.Result = "호스트 중단: 전송/결과 불확실. 자동 재전송 금지.";
                run.Evidence = new(CommandOutcome.Unknown, ConfirmationLevel.None, run.Result, Now);
                var target = job.Snapshot.Steps[index].Target;
                if (target is not null && next.Devices.Any(d => d.MatchesExecutionTarget(target)) &&
                    next.DeviceStates.TryGetValue(target.Id, out var recoveredDevice))
                {
                    recoveredDevice.ConnectionStatus = DeviceConnectionStatus.RecoveryRequired;
                    recoveredDevice.Connection = "호스트 중단 / 대상 상태 대조 필요";
                    recoveredDevice.LastResult = run.Result;
                    if (!next.UncertainDevices.Contains(target.Id)) next.UncertainDevices.Add(target.Id);
                }
            }
            // A never-dispatched manual command is durably queued and may proceed after revalidation.
            // Every interrupted scenario is stopped, including a wait before the first step.
            if (job.Kind is JobKind.Scenario or JobKind.LayoutDisplay || (job.IsLightBatch && job.Steps.Any(x => x.SentAt is not null)) ||
                job.Steps.Any(x => x.Status == StepStatus.Unknown) || job.Status == JobStatus.StopRequested)
            {
                foreach (var step in job.Steps.Where(x => x.Status is StepStatus.Pending or StepStatus.Waiting))
                { step.Status = StepStatus.Skipped; step.Result = "호스트 재시작: 자동 재개 금지"; }
                job.Status = job.Steps.Any(x => x.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Interrupted;
                job.Result = "재시작 복구: 기록 보존, 중단 작업 자동 재실행 없음";
            }
        }
        Audit(next, null, "HostStarted", "전송 중 명령 대조 및 중단 시나리오 자동 재개 차단");
        Persist(next);
    }
    public async Task<bool> DispatchNextAsync(CancellationToken hostStopping = default)
    {
        if (Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0) return false;
        try
        {
            Guid jobId;
            int stepIndex;
            StepSnapshot snapshot;
            lock (_gate)
            {
                if (_stopping || _storageFailed || hostStopping.IsCancellationRequested) return false;
                CheckConnectionUnsafe();
                var next = JsonDefaults.Copy(_state);
                if (CompleteLayoutSteps(next)) { Persist(next); return true; }
                var job = next.Jobs.Where(j => j.Status is JobStatus.Queued or JobStatus.Running &&
                        j.ReadyAt <= Now && !j.Steps.Any(s => s.Status == StepStatus.Dispatching))
                    .OrderByDescending(j => j.Kind == JobKind.Manual && j.Snapshot.Steps[0].Operation == DeviceOperation.Stop)
                    .ThenBy(j => j.Snapshot.AcceptedAt).FirstOrDefault();
                if (job is null) return false;
                stepIndex = job.Steps.FindIndex(x => x.Status is StepStatus.Pending or StepStatus.Waiting);
                if (stepIndex < 0) return false;
                jobId = job.Id; snapshot = job.Snapshot.Steps[stepIndex];
                var invalid = Revalidate(next, job, snapshot);
                if (invalid is not null)
                {
                    foreach (var step in job.Steps.Where(x => x.Status is StepStatus.Pending or StepStatus.Waiting))
                    { step.Status = StepStatus.Skipped; step.Result = invalid; step.FinishedAt = Now; }
                    job.Status = JobStatus.Interrupted; job.Result = $"전송 차단: {invalid}";
                    Audit(next, null, "DispatchRejected", $"job={job.Id}; {invalid}");
                    Persist(next); return true; // Never honor Continue for authorization/configuration failures.
                }
                if (snapshot.Kind == ScenarioStepKind.ShowLayout)
                { BeginLayoutDisplay(next, job, stepIndex); Persist(next); return true; }
                if (snapshot.Kind == ScenarioStepKind.WaitUntil)
                {
                    var waiting = job.Steps[stepIndex];
                    waiting.Status = StepStatus.Waiting; waiting.WaitStartedAt ??= Now;
                    waiting.WaitDeadline ??= Now.AddMilliseconds(snapshot.TimeoutMs);
                    job.Status = JobStatus.Running; Persist(next);
                }
                else
                {
                    var notBefore = DispatchNotBefore(next, snapshot);
                    if (notBefore > Now)
                    {
                        job.ReadyAt = notBefore;
                        job.Result = "장비 제약·공유 연결 전송 간격 대기";
                        Persist(next); return true;
                    }
                    var run = job.Steps[stepIndex];
                    run.Status = StepStatus.Dispatching; run.SentAt = Now; run.Result = "전송 의도 영속화 / 결과 대기";
                    job.Status = JobStatus.Running;
                    next.DeviceStates[snapshot.Target!.Id].Desired[snapshot.Operation] = snapshot.Value;
                    Audit(next, null, "DispatchIntent", $"job={job.Id}; step={stepIndex}; pc={snapshot.Target!.PcId}; device={snapshot.Target!.Id}");
                    Persist(next); // Linearization point: later cancellation cannot claim this step was never sent.
                }
            }
            if (snapshot.Kind == ScenarioStepKind.WaitUntil)
            { await PollConditionAsync(jobId, stepIndex, snapshot, hostStopping); return true; }
            DriverResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
            timeout.CancelAfter(snapshot.TimeoutMs);
            try
            {
                if (snapshot.ConditionOperation is { } condition)
                {
                    var reading = await _driver.ReadAsync(snapshot.Target!, timeout.Token);
                    if (!reading.Available)
                        result = new(StepStatus.Failed, "조건 상태 조회 실패");
                    else if (!ConditionSatisfied(snapshot, reading))
                        result = new(StepStatus.Failed, "최신 상태 확인 조건 불충족 · 관측 신선도/근거 확인");
                    else
                        result = await ExecuteIfStillAllowedAsync(jobId, stepIndex, snapshot, timeout.Token);
                }
                else result = await ExecuteIfStillAllowedAsync(jobId, stepIndex, snapshot, timeout.Token);
            }
            catch (OperationCanceledException)
            { result = new(StepStatus.Unknown, "제한시간 또는 호스트 종료: 결과 불확실. 자동 재전송 없음.")
                { Outcome = hostStopping.IsCancellationRequested ? CommandOutcome.Cancelled : CommandOutcome.TimedOut }; }
            catch (Exception)
            { result = new(StepStatus.Unknown, "드라이버 예외: 결과 불확실. 자동 재전송 없음."); }
            lock (_gate)
            {
                if (_storageFailed) return false;
                var next = JsonDefaults.Copy(_state);
                var job = FindJob(next, jobId);
                var run = job.Steps[stepIndex];
                result = ValidateResult(snapshot, result);
                run.Status = result.Status; run.Result = result.Detail; run.FinishedAt = Now;
                run.Evidence = EvidenceFor(snapshot, result);
                // Keep the original result in the job, but never attribute it to a replacement target.
                if (next.Devices.Any(d => d.MatchesExecutionTarget(snapshot.Target!)) &&
                    next.DeviceStates.TryGetValue(snapshot.Target!.Id, out var device))
                {
                    ApplyDriverState(device, snapshot, result);
                    if (result.Status == StepStatus.Unknown && !next.UncertainDevices.Contains(snapshot.Target!.Id))
                        next.UncertainDevices.Add(snapshot.Target!.Id);
                }
                FinishStep(next, job, stepIndex, result, run.Evidence);
                Persist(next);
            }
            return true;
        }
        finally { Volatile.Write(ref _dispatching, 0); }
    }
    private Task<DriverResult> ExecuteIfStillAllowedAsync(Guid jobId, int index, StepSnapshot step, CancellationToken ct)
    {
        lock (_gate)
        {
            var job = FindJob(_state, jobId);
            var invalid = Revalidate(_state, job, step);
            if (_stopping || job.CancelRequestedAt is not null || invalid is not null)
                return Task.FromResult(new DriverResult(StepStatus.Skipped, invalid ?? "전송 진입 전 취소/호스트 종료 확인"));
            // A slow condition read must not consume the interval before the command is actually invoked.
            var next = JsonDefaults.Copy(_state);
            next.ConnectionLastDispatchAt[step.Target!.ConnectionId] = Now;
            next.ConnectionNotBefore[step.Target!.ConnectionId] = Now.AddMilliseconds(CapabilityFor(step).MinimumCommandIntervalMs);
            Persist(next);
            // Invocation starts within the same coordination boundary as cancellation/configuration changes.
            return _driver.ExecuteAsync(step, ct);
        }
    }
}
