using IntegratedContro.Core;

namespace IntegratedContro.Application;

// The scenario runner can execute/read frozen device steps without knowing a driver or transport.
internal interface IDeviceScenarioOperations
{
    StepSnapshot Resolve(StateContext state, AccountView user, ScenarioStep step, RoleBinding? overrideRole = null);
    string? RevalidateTarget(StateContext state, Job job, StepSnapshot step);
    Task<StepExecutionResult> ExecuteAsync(StepSnapshot step, CancellationToken ct);
    Task<DriverReading> ReadAsync(DeviceConfig target, CancellationToken ct);
    bool ValidReading(DeviceConfig target, DriverReading reading);
    void RecordResult(StateContext state, StepSnapshot step, StepExecutionResult result);
    void RecordConditionReading(StateContext state, StepSnapshot step, DriverReading reading);
    LightLayout CurrentLightLayout(StateContext state);
    LightSlot ReadLightSlot(StateContext state, int number, int version);
    void RestoreLightSlotLayout(StateContext state, LightSlot slot, int expectedVersion);
    void ValidateCardPower(StateContext state, SubmitRequest request, StepSnapshot[] snapshots);
    void RecordDispatchIntent(StateContext context, StepSnapshot step);
    void MarkUncertain(StateContext context, IEnumerable<Guid> ids);
    void RecoverInterrupted(StateContext context, StepSnapshot step, string message);
}
