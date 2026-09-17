using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal static class ControlAuthorization
{
    internal static bool CanControl(Account account, Guid deviceId) =>
        account.Enabled && account.Role != AccountRole.Viewer && (account.AllDevices || account.DeviceIds.Contains(deviceId));
    internal static bool CanControlJob(Account user, Job job) => job.Snapshot.Steps.All(s =>
        s.Kind == ScenarioStepKind.DisplayLayout ? HiperwallPermission(user) : s.Target is { } target && CanControl(user, target.Id));
    internal static bool HiperwallPermission(Account account) => account.Enabled && account.Role != AccountRole.Viewer && account.AllDevices;
}
