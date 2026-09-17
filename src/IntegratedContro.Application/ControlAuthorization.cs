using IntegratedContro.Core;

namespace IntegratedContro.Application;

internal static class ControlAuthorization
{
    internal static bool CanControl(Account account, Guid deviceId) =>
        CanControl(new AccountView(account.Id, account.Name, account.Role, account.Enabled, account.AllDevices, account.DeviceIds.ToArray()), deviceId);
    internal static bool HiperwallPermission(Account account) => account.Enabled && account.Role != AccountRole.Viewer && account.AllDevices;
    internal static bool CanControl(AccountView account, Guid deviceId) =>
        account.Enabled && account.Role != AccountRole.Viewer && (account.AllDevices || account.DeviceIds.Contains(deviceId));
    internal static bool CanControlJob(AccountView user, Job job) => job.Snapshot.Steps.All(s =>
        s.Kind == ScenarioStepKind.DisplayLayout ? HiperwallPermission(user) : s.Target is { } target && CanControl(user, target.Id));
    internal static bool HiperwallPermission(AccountView account) => account.Enabled && account.Role != AccountRole.Viewer && account.AllDevices;
}
