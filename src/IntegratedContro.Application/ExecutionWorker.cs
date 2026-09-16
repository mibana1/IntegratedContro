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
                if (run.Status == StepStatus.Waiting)
                {
                    var display = next.HiperwallDisplays.SingleOrDefault(d => d.ScenarioJobId == job.Id && d.ScenarioStepIndex == index);
                    run.Status = display?.Targets.Any(t => t.OpenState is HiperwallSendState.Sending or HiperwallSendState.Unknown) == true
                        ? StepStatus.Unknown : StepStatus.Skipped;
                    run.Result = "호스트 재시작: 대기·시나리오 자동 재개 금지 / 표시 기록 유지"; run.FinishedAt = Now;
                    continue;
                }
                if (run.Status != StepStatus.Dispatching) continue;
                run.Status = StepStatus.Unknown; run.Result = "호스트 중단: 전송/결과 불확실. 자동 재전송 금지.";
                var target = job.Snapshot.Steps[index].Target;
                if (target is not null && next.Devices.Any(d => d.MatchesExecutionTarget(target)) &&
                    next.DeviceStates.TryGetValue(target.Id, out var recoveredDevice))
                {
                    recoveredDevice.Connection = "호스트 중단 / 가상 상태 대조 필요"; recoveredDevice.LastResult = run.Result;
                    if (!next.UncertainDevices.Contains(target.Id)) next.UncertainDevices.Add(target.Id);
                }
            }
            if (job.Kind == JobKind.Scenario || (job.IsLightBatch && job.Steps.Any(x => x.SentAt is not null)) ||
                job.Steps.Any(x => x.Status == StepStatus.Unknown) || job.Status == JobStatus.StopRequested)
            {
                StopScenarioPendingDisplays(next, job, "호스트 재시작: 미전송 표시 자동 재개 금지");
                foreach (var step in job.Steps.Where(x => x.Status == StepStatus.Pending))
                { step.Status = StepStatus.Skipped; step.Result = "호스트 재시작: 자동 재개 금지"; }
                job.Status = job.Steps.Any(x => x.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Interrupted;
                job.Result = "재시작 복구: 기록 보존, 중단 작업 자동 재실행 없음";
            }
        }
        RecoverHiperwallDisplays(next);
        Audit(next, null, "HostStarted", "전송 중 명령 대조 및 중단 시나리오 자동 재개 차단");
        Persist(next);
    }
    public async Task<bool> DispatchNextAsync(CancellationToken hostStopping = default)
    {
        if (Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0) return false;
        try
        {
            Guid jobId; int stepIndex; StepSnapshot snapshot;
            lock (_gate)
            {
                if (_stopping || _storageFailed || hostStopping.IsCancellationRequested) return false;
                CheckConnectionUnsafe();
                var next = JsonDefaults.Copy(_state);
                var job = next.Jobs.Where(j => j.Active && j.ReadyAt <= Now &&
                        j.Steps.Any(s => s.Status is StepStatus.Pending or StepStatus.Waiting))
                    .OrderByDescending(j => j.Kind == JobKind.Manual && j.Snapshot.Steps[0].Operation == DeviceOperation.Stop)
                    .ThenBy(j => j.ReadyAt).ThenBy(j => j.Snapshot.AcceptedAt).FirstOrDefault();
                if (job is null) return false;
                stepIndex = job.Steps.FindIndex(x => x.Status is StepStatus.Pending or StepStatus.Waiting);
                jobId = job.Id; snapshot = job.Snapshot.Steps[stepIndex];
                var invalid = Revalidate(next, job, snapshot);
                if (invalid is not null && job.CancelRequestedAt is null)
                {
                    StopScenarioPendingDisplays(next, job, invalid);
                    // An in-flight display may already have been transmitted. Preserve it as unknown.
                    var uncertain = next.HiperwallDisplays.Any(d => d.ScenarioJobId == jobId &&
                        d.Targets.Any(t => t.OpenState is HiperwallSendState.Sending or HiperwallSendState.Unknown));
                    FinishStep(next, job, stepIndex, new(uncertain ? StepStatus.Unknown : StepStatus.Skipped, "전송 차단: " + invalid));
                    Persist(next); return true;
                }
                var run = job.Steps[stepIndex];
                if (run.Status == StepStatus.Pending)
                {
                    run.StartedAt = Now;
                    run.Status = snapshot.Kind == ScenarioStepKind.DeviceCommand ? StepStatus.Dispatching : StepStatus.Waiting;
                    if (snapshot.Kind == ScenarioStepKind.DeviceCommand)
                    {
                        run.SentAt = Now; run.Result = "전송 의도 영속화 / 결과 대기";
                        next.DeviceStates[snapshot.Target!.Id].Desired[snapshot.Operation] = snapshot.Value;
                    }
                    else { run.DeadlineAt = Now.AddMilliseconds(snapshot.TimeoutMs); run.Result = "단계 시작 / 확인 대기"; }
                    job.Status = JobStatus.Running;
                    Audit(next, null, "DispatchIntent", $"job={job.Id}; step={stepIndex}; kind={snapshot.Kind}");
                    Persist(next);
                }
            }
            DriverResult? result;
            if (snapshot.Kind == ScenarioStepKind.WaitUntil)
                result = await PollScenarioConditionAsync(jobId, stepIndex, snapshot, hostStopping);
            else if (snapshot.Kind == ScenarioStepKind.DisplayLayout)
                result = await PollScenarioDisplayAsync(jobId, stepIndex, snapshot, hostStopping);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostStopping); timeout.CancelAfter(snapshot.TimeoutMs);
                try
                {
                    if (snapshot.ConditionOperation is { } condition)
                    {
                        var reading = await _driver.ReadAsync(snapshot.Target!, timeout.Token);
                        if (!reading.Available) result = new(StepStatus.Failed, "가상 조건 상태 조회 실패");
                        else if (!reading.Values.TryGetValue(condition, out var value) || value != snapshot.ConditionValue)
                            result = new(StepStatus.Failed, "최신 가상 상태 확인 조건 불충족");
                        else result = await ExecuteIfStillAllowedAsync(jobId, stepIndex, snapshot, timeout.Token);
                    }
                    else result = await ExecuteIfStillAllowedAsync(jobId, stepIndex, snapshot, timeout.Token);
                }
                catch (OperationCanceledException) { result = new(StepStatus.Unknown, "제한시간 또는 호스트 종료: 결과 불확실. 자동 재전송 없음."); }
                catch (Exception) { result = new(StepStatus.Unknown, "드라이버 예외: 결과 불확실. 자동 재전송 없음."); }
            }
            if (result is null) return true;
            lock (_gate)
            {
                if (_storageFailed) return false;
                var next = JsonDefaults.Copy(_state); var job = FindJob(next, jobId);
                // A cancelled read-only wait cannot be resurrected by a late reading.
                if (job.Steps[stepIndex].Status is not (StepStatus.Dispatching or StepStatus.Waiting)) return true;
                if (snapshot.Kind == ScenarioStepKind.DeviceCommand && snapshot.Target is { } target &&
                    next.Devices.Any(d => d.MatchesExecutionTarget(target)) && next.DeviceStates.TryGetValue(target.Id, out var device))
                {
                    device.LastResult = result.Detail;
                    device.Connection = result.Status == StepStatus.Simulated ? "가상 연결됨" : "가상 오류/대조 필요";
                    if (result.Values is not null)
                        foreach (var pair in result.Values) device.Simulated[pair.Key] = new(pair.Value, Now);
                    if (result.Status == StepStatus.Unknown && !next.UncertainDevices.Contains(target.Id)) next.UncertainDevices.Add(target.Id);
                }
                FinishStep(next, job, stepIndex, result); Persist(next);
            }
            return true;
        }
        finally { Volatile.Write(ref _dispatching, 0); }
    }
    private void FinishStep(HostState next, Job job, int index, DriverResult result)
    {
        var run = job.Steps[index]; var snapshot = job.Snapshot.Steps[index];
        run.Status = result.Status; run.Result = result.Detail; run.FinishedAt = Now;
        var cancelled = job.CancelRequestedAt is not null;
        var success = result.Status is StepStatus.Simulated or StepStatus.ConditionMet or StepStatus.Acknowledged;
        var stop = cancelled || result.Status is StepStatus.Unknown or StepStatus.Skipped || (!success && snapshot.OnFailure == FailurePolicy.Stop);
        if (!success) StopScenarioPendingDisplays(next, job, result.Detail);
        if (stop)
        {
            foreach (var pending in job.Steps.Where(x => x.Status is StepStatus.Pending or StepStatus.Waiting))
            { pending.Status = StepStatus.Skipped; pending.Result = "후속 단계 차단"; pending.FinishedAt = Now; }
            job.Status = result.Status == StepStatus.Unknown ? JobStatus.NeedsReview : cancelled ? JobStatus.Cancelled : JobStatus.Interrupted;
            job.Result = cancelled ? "미전송·대기 부분 취소 완료 / 전송 결과·열린 표시 보존. 물리 정지·롤백 아님." : result.Detail;
        }
        else
        {
            var nextIndex = job.Steps.FindIndex(x => x.Status == StepStatus.Pending);
            job.Status = nextIndex < 0 ? JobStatus.Completed : JobStatus.Running;
            job.Result = nextIndex < 0 ? (job.Steps.Any(x => x.Status == StepStatus.Failed) ? "순차 실행 종료 / 실패 단계 포함" :
                job.Snapshot.Mode == "Mixed" ? "순차 실행 완료 / 가상 상태·Controller 응답 확인, 물리 표시 검증 아님" : "가상 순차 실행 완료 / 실측 아님")
                : $"단계 {index + 1}/{job.Steps.Count} 종료";
            if (nextIndex >= 0) job.ReadyAt = Now.AddMilliseconds(job.Snapshot.Steps[nextIndex].DelayBeforeMs);
        }
        Audit(next, null, "DispatchResult", $"job={job.Id}; step={index}; result={run.Status}");
    }
    private Task<DriverResult> ExecuteIfStillAllowedAsync(Guid jobId, int index, StepSnapshot step, CancellationToken ct)
    {
        lock (_gate)
        {
            var job = FindJob(_state, jobId); var invalid = Revalidate(_state, job, step);
            if (_stopping || job.CancelRequestedAt is not null || invalid is not null)
                return Task.FromResult(new DriverResult(StepStatus.Skipped, invalid ?? "전송 진입 전 취소/호스트 종료 확인"));
            return _driver.ExecuteAsync(step, ct);
        }
    }
}
