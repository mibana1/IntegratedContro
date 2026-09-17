using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private void StopJob(HostState state, Job job, Session session, string reason) => _jobs.StopJob(state, job, session, reason);
    private void FinishStep(HostState state, Job job, int index, DriverResult result) => _jobs.FinishStep(state, job, index, result);
    private void StopScenarioPendingDisplays(HostState state, Job job, string reason) => _jobs.StopScenarioPendingDisplays(state, job, reason);
}
