using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

// The scenario runner can execute/read frozen device steps without knowing a driver or transport.
internal interface IDeviceScenarioOperations
{
    StepSnapshot Resolve(StateContext state, AccountView user, ScenarioStep step, RoleBinding? overrideRole = null);
    string? RevalidateTarget(StateContext state, Job job, StepSnapshot step);
    Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken ct);
    Task<DriverReading> ReadAsync(DeviceConfig target, CancellationToken ct);
    bool ValidReading(DeviceConfig target, DriverReading reading);
    DriverResult NormalizeResult(StepSnapshot step, DriverResult result);
    void RecordResult(StateContext state, StepSnapshot step, DriverResult result);
    void RecordConditionReading(StateContext state, StepSnapshot step, DriverReading reading);
    LightLayout CurrentLightLayout(StateContext state);
    void ValidateCardPower(StateContext state, SubmitRequest request, StepSnapshot[] snapshots);
    void RecordDispatchIntent(StateContext context, StepSnapshot step);
    void MarkUncertain(StateContext context, IEnumerable<Guid> ids);
    void RecoverInterrupted(StateContext context, StepSnapshot step, string message);
}
