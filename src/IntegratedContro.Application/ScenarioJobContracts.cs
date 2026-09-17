using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

// Shared transaction operations for parent jobs; a display result cannot bypass cancellation policy.
internal interface IScenarioJobLifecycle
{
    void StopJob(HostState state, Job job, Session session, string reason);
    void FinishStep(HostState state, Job job, int index, DriverResult result);
    void StopScenarioPendingDisplays(HostState state, Job job, string reason);
}
