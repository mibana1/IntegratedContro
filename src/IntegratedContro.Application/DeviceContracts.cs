using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

// The scenario runner can execute/read frozen device steps without knowing a driver or transport.
internal interface IDeviceScenarioOperations
{
    StepSnapshot Resolve(HostState state, Account user, ScenarioStep step, RoleBinding? overrideRole = null);
    string? RevalidateTarget(HostState state, Job job, StepSnapshot step);
    Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken ct);
    Task<DriverReading> ReadAsync(DeviceConfig target, CancellationToken ct);
    bool ValidReading(DeviceConfig target, DriverReading reading);
    DriverResult NormalizeResult(StepSnapshot step, DriverResult result);
    void RecordResult(HostState state, StepSnapshot step, DriverResult result);
    void RecordConditionReading(HostState state, StepSnapshot step, DriverReading reading);
    LightLayout CurrentLightLayout(HostState state);
    void ValidateCardPower(HostState state, SubmitRequest request, StepSnapshot[] snapshots);
}
