using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class ScenarioJobLifecycle : IScenarioJobLifecycle
{
    private readonly HostAuthority _host;
    internal ScenarioJobLifecycle(HostAuthority host) => _host = host;
    public void StopJob(HostState s, Job job, Session session, string reason)
    {
        if (!job.Active && job.Status != JobStatus.NeedsReview) return;
        job.CancelledBy ??= session.Info.UserId;
        job.CancellerName ??= session.Info.UserName;
        job.CancelRequestedAt ??= _host.Now;
        StopScenarioPendingDisplays(s, job, "시나리오 중단: 미전송 표시 차단");
        foreach (var (step, index) in job.Steps.Select((value, index) => (value, index)))
        {
            var display = s.HiperwallDisplays.SingleOrDefault(d => d.ScenarioJobId == job.Id && d.ScenarioStepIndex == index);
            if (step.Status == StepStatus.Waiting && display?.Targets.Any(t => t.OpenState == HiperwallSendState.Sending) == true)
                continue; // Keep reservations until an in-flight open has a recorded outcome.
            if (step.Status is StepStatus.Pending or StepStatus.Waiting)
            {
                step.Status = display?.Targets.Any(t => t.OpenState == HiperwallSendState.Unknown) == true ? StepStatus.Unknown : StepStatus.Skipped;
                step.Result = "미전송·대기 부분 취소 / 이미 열린 표시는 정리 일정 유지"; step.FinishedAt = _host.Now;
            }
        }
        job.Status = job.Steps.Any(x => x.Status is StepStatus.Dispatching or StepStatus.Waiting) ? JobStatus.StopRequested :
            job.Steps.Any(x => x.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Cancelled;
        job.ReadyAt = _host.Now;
        job.Result = job.Status == JobStatus.StopRequested ? "취소 요청 / 이미 전송된 단계 결과 대기. 물리 정지·롤백 아님."
            : "미전송·대기 부분 취소 완료. 이미 열린 표시와 전송 결과는 유지합니다.";
        _host.Audit(s, session.Info.UserId, "JobCancellation", $"job={job.Id}; {reason}; status={job.Status}");
    }
    public void FinishStep(HostState next, Job job, int index, DriverResult result)
    {
        var run = job.Steps[index]; var snapshot = job.Snapshot.Steps[index];
        run.Status = result.Status; run.Result = result.Detail; run.FinishedAt = _host.Now;
        var cancelled = job.CancelRequestedAt is not null;
        var success = result.Status is StepStatus.Simulated or StepStatus.ConditionMet or StepStatus.Acknowledged or StepStatus.Sent or StepStatus.Observed;
        var stop = cancelled || result.Status is StepStatus.Unknown or StepStatus.Skipped || (!success && snapshot.OnFailure == FailurePolicy.Stop);
        if (!success) StopScenarioPendingDisplays(next, job, result.Detail);
        if (stop)
        {
            foreach (var pending in job.Steps.Where(x => x.Status is StepStatus.Pending or StepStatus.Waiting))
            { pending.Status = StepStatus.Skipped; pending.Result = "후속 단계 차단"; pending.FinishedAt = _host.Now; }
            job.Status = result.Status == StepStatus.Unknown ? JobStatus.NeedsReview : cancelled ? JobStatus.Cancelled : JobStatus.Interrupted;
            job.Result = cancelled ? "미전송·대기 부분 취소 완료 / 전송 결과·열린 표시 보존. 물리 정지·롤백 아님." : result.Detail;
        }
        else
        {
            var nextIndex = job.Steps.FindIndex(x => x.Status == StepStatus.Pending);
            job.Status = nextIndex < 0 ? JobStatus.Completed : JobStatus.Running;
            job.Result = nextIndex < 0 ? (job.Steps.Any(x => x.Status == StepStatus.Failed) ? "순차 실행 종료 / 실패 단계 포함" :
                job.Snapshot.Mode == "Virtual" ? "가상 순차 실행 완료 / 실측 아님" : "순차 실행 종료 / 단계별 전송·응답·관측 결과를 확인하세요")
                : $"단계 {index + 1}/{job.Steps.Count} 종료";
            if (nextIndex >= 0) job.ReadyAt = _host.Now.AddMilliseconds(job.Snapshot.Steps[nextIndex].DelayBeforeMs);
        }
        _host.Audit(next, null, "DispatchResult", $"job={job.Id}; step={index}; result={run.Status}");
    }
    public void StopScenarioPendingDisplays(HostState state, Job job, string reason)
    {
        foreach (var display in state.HiperwallDisplays.Where(d => d.ScenarioJobId == job.Id))
            foreach (var t in display.Targets.Where(t => t.OpenState == HiperwallSendState.Pending))
            { t.OpenState = HiperwallSendState.Rejected; t.CleanupState = DisplayCleanupState.Closed; t.Message = reason; }
    }
}
