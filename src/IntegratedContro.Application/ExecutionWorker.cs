using IntegratedContro.Core;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class ScenarioService
{
    internal void RecoverJobs(StateContext context)
    {
        var next = _host.For(context);
        foreach (var job in next.Jobs.Where(j => j.Active))
        {
            foreach (var (run, index) in job.Steps.Select((x, i) => (x, i)))
            {
                if (run.Status == StepStatus.Waiting)
                {
                    var display = _jobs.DisplayProgress(next, job.Id, index);
                    run.Status = display.Sending || display.Unknown
                        ? StepStatus.Unknown : StepStatus.Skipped;
                    run.Result = "호스트 재시작: 대기·시나리오 자동 재개 금지 / 표시 기록 유지"; run.FinishedAt = _host.Now;
                    continue;
                }
                if (run.Status != StepStatus.Dispatching) continue;
                run.Status = StepStatus.Unknown; run.Result = "호스트 중단: 전송/결과 불확실. 자동 재전송 금지.";
                _devices.RecoverInterrupted(next, job.Snapshot.Steps[index], run.Result);
            }
            if (job.Kind == JobKind.Scenario || (job.IsLightBatch && job.Steps.Any(x => x.SentAt is not null)) ||
                job.Steps.Any(x => x.Status == StepStatus.Unknown) || job.Status == JobStatus.StopRequested)
            {
                _jobs.StopScenarioPendingDisplays(next, job.Id, "호스트 재시작: 미전송 표시 자동 재개 금지");
                foreach (var step in job.Steps.Where(x => x.Status == StepStatus.Pending))
                { step.Status = StepStatus.Skipped; step.Result = "호스트 재시작: 자동 재개 금지"; }
                job.Status = job.Steps.Any(x => x.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Interrupted;
                job.Result = "재시작 복구: 기록 보존, 중단 작업 자동 재실행 없음";
            }
        }
    }
    public async Task<bool> DispatchNextAsync(CancellationToken hostStopping = default)
    {
        if (Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0) return false;
        try
        {
            Guid jobId; int stepIndex; StepSnapshot snapshot;
            using (_host.Open())
            {
                if (_host.Stopping || _host.StorageFailed || hostStopping.IsCancellationRequested) return false;
                _host.CheckConnections();
                var next = _host.Draft();
                var job = next.Jobs.Where(j => j.Active && j.ReadyAt <= _host.Now &&
                        j.Steps.Any(s => s.Status is StepStatus.Pending or StepStatus.Waiting))
                    .OrderByDescending(j => j.Kind == JobKind.Manual && j.Snapshot.Steps[0].Operation == DeviceOperation.Stop)
                    .ThenBy(j => j.ReadyAt).ThenBy(j => j.Snapshot.AcceptedAt).FirstOrDefault();
                if (job is null) return false;
                stepIndex = job.Steps.FindIndex(x => x.Status is StepStatus.Pending or StepStatus.Waiting);
                jobId = job.Id; snapshot = job.Snapshot.Steps[stepIndex];
                var invalid = Revalidate(next, job, snapshot);
                if (invalid is not null && job.CancelRequestedAt is null)
                {
                    _jobs.StopScenarioPendingDisplays(next, job.Id, invalid);
                    // An in-flight display may already have been transmitted. Preserve it as unknown.
                    var display = _jobs.DisplayProgress(next, jobId);
                    var uncertain = display.Sending || display.Unknown;
                    _jobs.FinishStep(next, job.Id, stepIndex, new(uncertain ? StepStatus.Unknown : StepStatus.Skipped, "전송 차단: " + invalid));
                    _host.Commit(next); return true;
                }
                var run = job.Steps[stepIndex];
                if (run.Status == StepStatus.Pending)
                {
                    run.StartedAt = _host.Now;
                    run.Status = snapshot.Kind == ScenarioStepKind.DeviceCommand ? StepStatus.Dispatching : StepStatus.Waiting;
                    if (snapshot.Kind == ScenarioStepKind.DeviceCommand)
                    {
                        run.SentAt = _host.Now; run.Result = "전송 의도 영속화 / 결과 대기";
                        _devices.RecordDispatchIntent(next, snapshot);
                    }
                    else { run.DeadlineAt = _host.Now.AddMilliseconds(snapshot.TimeoutMs); run.Result = "단계 시작 / 확인 대기"; }
                    job.Status = JobStatus.Running;
                    _host.Audit(next, null, "DispatchIntent", $"job={job.Id}; step={stepIndex}; kind={snapshot.Kind}");
                    _host.Commit(next);
                }
            }
            StepExecutionResult? result;
            if (snapshot.Kind == ScenarioStepKind.WaitUntil)
                result = await PollScenarioConditionAsync(jobId, stepIndex, snapshot, hostStopping);
            else if (snapshot.Kind == ScenarioStepKind.DisplayLayout)
                result = await _displays.PollScenarioDisplayAsync(jobId, stepIndex, snapshot, hostStopping);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostStopping); timeout.CancelAfter(snapshot.TimeoutMs);
                try
                {
                    if (snapshot.ConditionOperation is { } condition)
                    {
                        var reading = await _devices.ReadAsync(snapshot.Target!, timeout.Token);
                        if (!_devices.ValidReading(snapshot.Target!, reading)) result = new(StepStatus.Failed, "조건 상태 조회 실패 / 관측 증거 없음");
                        else if (!reading.Values.TryGetValue(condition, out var value) || value != snapshot.ConditionValue)
                            result = new(StepStatus.Failed, "최신 상태 확인 조건 불충족");
                        else result = await ExecuteIfStillAllowedAsync(jobId, stepIndex, snapshot, timeout.Token);
                    }
                    else result = await ExecuteIfStillAllowedAsync(jobId, stepIndex, snapshot, timeout.Token);
                }
                catch (OperationCanceledException) { result = new(StepStatus.Unknown, "제한시간 또는 호스트 종료: 결과 불확실. 자동 재전송 없음."); }
                catch (Exception) { result = new(StepStatus.Unknown, "드라이버 예외: 결과 불확실. 자동 재전송 없음."); }
            }
            if (result is null) return true;
            using (_host.Open())
            {
                if (_host.StorageFailed) return false;
                var next = _host.Draft(); var job = FindJob(next.Jobs, jobId);
                // A cancelled read-only wait cannot be resurrected by a late reading.
                if (job.Steps[stepIndex].Status is not (StepStatus.Dispatching or StepStatus.Waiting)) return true;
                _devices.RecordResult(next, snapshot, result);
                _jobs.FinishStep(next, job.Id, stepIndex, result); _host.Commit(next);
            }
            return true;
        }
        finally { Volatile.Write(ref _dispatching, 0); }
    }
    private Task<StepExecutionResult> ExecuteIfStillAllowedAsync(Guid jobId, int index, StepSnapshot step, CancellationToken ct)
    {
        using (_host.Open())
        {
            var job = FindJob(_host.Current.Jobs, jobId); var invalid = Revalidate(_host.Current, job, step);
            if (_host.Stopping || _host.StorageFailed || job.CancelRequestedAt is not null || invalid is not null)
                return Task.FromResult(new StepExecutionResult(StepStatus.Skipped, invalid ?? "전송 진입 전 취소/저장 실패/호스트 종료 확인"));
            return _devices.ExecuteAsync(step, ct);
        }
    }
}
