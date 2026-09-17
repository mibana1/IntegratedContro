using IntegratedContro.Core;

namespace IntegratedContro.Application;

// Wall-specific transitions run on the parent's transaction, without exposing display writes to it.
internal sealed class HiperwallJobLifecycle(IHiperwallStateAccess state) : IHiperwallJobLifecycle
{
    public void StopPending(StateContext context, Guid jobId, string reason)
    {
        foreach (var display in state.For(context).HiperwallDisplays.Where(d => d.ScenarioJobId == jobId))
            foreach (var t in display.Targets.Where(t => t.OpenState == HiperwallSendState.Pending))
            { t.OpenState = HiperwallSendState.Rejected; t.CleanupState = DisplayCleanupState.Closed; t.Message = reason; }
    }
    public ScenarioDisplayProgress Progress(StateContext context, Guid jobId, int? index)
    {
        var targets = state.For(context).HiperwallDisplays.Where(d => d.ScenarioJobId == jobId &&
            (index is null || d.ScenarioStepIndex == index)).SelectMany(d => d.Targets).ToArray();
        return new(targets.Any(t => t.OpenState == HiperwallSendState.Sending), targets.Any(t => t.OpenState == HiperwallSendState.Unknown));
    }
}
