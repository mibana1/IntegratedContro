using IntegratedContro.Core;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public DeviceConfig SaveDeviceDiagnostics(string token, DeviceDiagnosticsRequest request) => _devices.SaveDiagnostics(token, request);
    public PcRegistration RegisterSessionPc(string token, RegisterSessionPcRequest request) => _devices.RegisterSessionPc(token, request);
    public DeviceConfig SaveDevice(string token, DeviceRequest request) => _devices.SaveDevice(token, request);
    public RoleBinding SaveRole(string token, RoleRequest request) => _devices.SaveRole(token, request);
    public bool UnassignRole(string token, UnassignRoleRequest request) => _devices.UnassignRole(token, request);
    public RoleBinding RenameRole(string token, RenameRoleRequest request) => _devices.RenameRole(token, request);
    public bool DeleteRole(string token, DeleteRoleRequest request) => _devices.DeleteRole(token, request);
    public LightLayout SaveLightOrder(string token, LightOrderRequest request) => _devices.SaveLightOrder(token, request);
    public Task<DeviceState> ReconcileAsync(string token, ReconcileRequest request, CancellationToken ct = default) => _devices.ReconcileAsync(token, request, ct);
}
