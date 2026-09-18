using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    // Only this adapter knows the shell and HTTP routes. Feature tests replace its typed contracts.
    private sealed class FeatureHost(MainViewModel owner) : ILightingHost, IScenarioHost, IDeviceSettingsHost, IAccountManagementHost, IJobManagementHost, IRecoveryHost
    {
        public Task<DeviceConfig> SaveDeviceDiagnosticsAsync(DeviceDiagnosticsRequest request) => owner.Client.Post<DeviceConfig>("/api/devices/diagnostics", request);
        public Task<PcRegistration> RegisterSessionPcAsync(RegisterSessionPcRequest request) => owner.Client.Post<PcRegistration>("/api/pcs/register-session", request);
        public Task<DeviceConfig> SaveDeviceAsync(DeviceRequest request) => owner.Client.Post<DeviceConfig>("/api/devices", request);
        public Task<RoleBinding> SaveRoleAsync(RoleRequest request) => owner.Client.Post<RoleBinding>("/api/roles", request);
        public Task<RoleBinding> CreateRoleAsync(CreateRoleRequest request) => owner.Client.Post<RoleBinding>("/api/roles/create", request);
        public Task<RoleBinding> RenameRoleAsync(RenameRoleRequest request) => owner.Client.Post<RoleBinding>("/api/roles/rename", request);
        public async Task DeleteRoleAsync(DeleteRoleRequest request) => await owner.Client.Post<bool>("/api/roles/delete", request);
        public async Task UnassignRoleAsync(UnassignRoleRequest request) => await owner.Client.Post<bool>("/api/roles/unassign", request);
        public Task RefreshAsync() => owner.Refresh();
        public async Task CreateAccountAsync(CreateAccountRequest request) => await owner.Client.Post<AccountView>("/api/accounts", request);
        public async Task UpdateAccountAsync(UpdateAccountRequest request) => await owner.Client.Post<bool>("/api/accounts/permissions", request);
        public async Task CancelJobAsync(JobActionRequest request) => await owner.Client.Post<Job>("/api/jobs/cancel", request);
        public async Task SwitchToManualAsync(JobActionRequest request) => await owner.Client.Post<Job>("/api/jobs/manual-switch", request);
        public async Task StopDisplayAsync(JobActionRequest request) => await owner.Client.Post<HiperwallDisplayJob>("/api/hiperwall/displays/stop", request);
        public Task<HiperwallEditReceipt> CancelEditAsync(JobActionRequest request) => owner.Client.Post<HiperwallEditReceipt>("/api/hiperwall/edits/cancel", request);
        public Task<RecoveryReview> ReviewAsync() => owner.Client.Post<RecoveryReview>("/api/recovery/review");
        public async Task ApproveRecoveryAsync(RecoveryApprovalRequest request) => await owner.Client.Post<Lease>("/api/recovery/approve", request);
        public Task RunAsync(Func<Task> action) => owner.RunCommand(action);
        public void ReportStatus(string message) => owner.Message = message;
        public Task<LightLayout> SaveLayoutAsync(LightOrderRequest request) => owner.Client.Post<LightLayout>("/api/layout/lights", request);
        public Task<DeviceState> ReconcileAsync(ReconcileRequest request) => owner.Client.Post<DeviceState>("/api/devices/reconcile", request);
        public Task<ScenarioDefinition> SaveAsync(ScenarioRequest request) => owner.Client.Post<ScenarioDefinition>("/api/scenarios", request);
        public async Task DeleteAsync(DeleteScenarioRequest request) => await owner.Client.Post<bool>("/api/scenarios/delete", request);
        public Task SubmitAsync(SubmitRequest request)
        {
            if (owner.HasPending) throw new InvalidOperationException("이전 요청의 접수 여부를 먼저 확인하세요.");
            owner._pending = request;
            owner.Notify();
            return owner.SendPending();
        }
        public Task SubmitBatchAsync(LightBatchRequest request)
        {
            if (owner.HasPending) throw new InvalidOperationException("이전 요청의 접수 여부를 먼저 확인하세요.");
            owner._pendingLightBatch = request;
            owner.Notify();
            return owner.SendPending();
        }
    }
}
