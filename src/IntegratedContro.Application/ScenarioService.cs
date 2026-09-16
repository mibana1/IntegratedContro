using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private static bool CanControlJob(Account user, Job job) => job.Snapshot.Steps.All(s =>
        s.Kind == ScenarioStepKind.DisplayLayout ? HiperwallPermission(user) : s.Target is { } target && CanControl(user, target.Id));
    private static void RequireHiperwallScenarioAvailable(HostState state)
    {
        Require(!state.Jobs.Any(j => j.Active && j.Kind == JobKind.Scenario && j.Snapshot.Steps.Any(s => s.Kind == ScenarioStepKind.DisplayLayout)),
            "hiperwall_reserved", "시나리오가 Hiperwall을 예약하고 있습니다. 작업·교대에서 시나리오 중단 후 수동 전환을 선택하세요.");
        Require(!state.HiperwallDisplays.Any(d => d.ScenarioJobId is not null && d.Targets.Any(t => t.Outstanding && t.OpenState is HiperwallSendState.Sending or HiperwallSendState.Unknown)),
            "hiperwall_uncertain", "이전 시나리오의 불확실한 표시를 먼저 목록 대조·정리하세요.");
    }
    private void StopScenarioPendingDisplays(HostState state, Job job, string reason)
    {
        foreach (var display in state.HiperwallDisplays.Where(d => d.ScenarioJobId == job.Id))
            foreach (var t in display.Targets.Where(t => t.OpenState == HiperwallSendState.Pending))
            { t.OpenState = HiperwallSendState.Rejected; t.CleanupState = DisplayCleanupState.Closed; t.Message = reason; }
    }
    private bool ScenarioAllowsDisplay(HiperwallDisplayJob display)
    {
        if (display.ScenarioJobId is not { } id) return true;
        var parent = _state.Jobs.SingleOrDefault(j => j.Id == id);
        return parent is { Active: true, CancelRequestedAt: null } && display.ScenarioStepIndex is { } index &&
            index >= 0 && index < parent.Steps.Count && parent.Steps[index].Status == StepStatus.Waiting &&
            Now < parent.Steps[index].DeadlineAt &&
            Revalidate(_state, parent, parent.Snapshot.Steps[index]) is null &&
            !display.Targets.Any(t => t.OpenState is HiperwallSendState.Rejected or HiperwallSendState.Unknown);
    }
}
