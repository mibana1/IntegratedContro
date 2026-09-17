using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

// Scenario display operations retain inventory, endpoint and write-lane details inside Hiperwall.
internal interface IScenarioDisplayOperations
{
    StepSnapshot ResolveDisplay(HostState state, Account user, ScenarioStep step);
    string? RevalidateDisplay(HostState state, Job job, StepSnapshot step);
    void ValidateScenarioAdmission(HostState state);
    Task<DriverResult?> PollScenarioDisplayAsync(Guid id, int index, StepSnapshot step, CancellationToken ct);
}
