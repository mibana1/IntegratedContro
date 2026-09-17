using IntegratedContro.Core;

namespace IntegratedContro.Application;

// Shared transaction operations for parent jobs; a display result cannot bypass cancellation policy.
internal interface IScenarioJobLifecycle
{
    void StopJob(StateContext state, Guid jobId, Session session, string reason);
    void FinishStep(StateContext state, Guid jobId, int index, StepExecutionResult result);
    void StopScenarioPendingDisplays(StateContext state, Guid jobId, string reason);
    ScenarioDisplayProgress DisplayProgress(StateContext context, Guid jobId, int? index = null);
    void ReportDisplayWaiting(StateContext context, Guid jobId, int index, string message, bool admitted = false);
}
internal sealed record StepExecutionResult(StepStatus Status, string Detail,
    IReadOnlyDictionary<DeviceOperation, int>? Values = null, DeviceEvidence Evidence = DeviceEvidence.Simulation);
internal sealed record ScenarioDisplayProgress(bool Sending, bool Unknown);
internal interface IHiperwallJobLifecycle
{
    void StopPending(StateContext context, Guid jobId, string reason);
    ScenarioDisplayProgress Progress(StateContext context, Guid jobId, int? index);
}
