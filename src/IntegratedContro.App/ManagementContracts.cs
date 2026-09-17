using IntegratedContro.Core;

namespace IntegratedContro.App;

public interface IDeviceSettingsHost : IFeatureSession
{
    Task<DeviceConfig> SaveDeviceAsync(DeviceRequest request);
    Task<RoleBinding> SaveRoleAsync(RoleRequest request);
    Task UnassignRoleAsync(UnassignRoleRequest request);
    Task<DeviceState> ReconcileAsync(ReconcileRequest request);
    Task RefreshAsync();
}

public interface IAccountManagementHost : IFeatureSession
{
    Task CreateAccountAsync(CreateAccountRequest request);
    Task UpdateAccountAsync(UpdateAccountRequest request);
}

public interface IJobManagementHost : IFeatureSession
{
    Task CancelJobAsync(JobActionRequest request);
    Task SwitchToManualAsync(JobActionRequest request);
    Task StopDisplayAsync(JobActionRequest request);
    Task<HiperwallEditReceipt> CancelEditAsync(JobActionRequest request);
}

public interface IRecoveryHost : IFeatureSession
{
    Task<RecoveryReview> ReviewAsync();
    Task ApproveRecoveryAsync(RecoveryApprovalRequest request);
}
