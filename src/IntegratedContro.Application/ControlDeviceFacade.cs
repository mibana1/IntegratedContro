using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private static string ExecutionMode(StepSnapshot[] steps) => AcceptedJobRules.ExecutionMode(steps);
    private bool ValidReading(DeviceConfig target, DriverReading reading) => _devices.ValidReading(target, reading);
    private LightLayout CurrentLightLayout(HostState state) => _devices.CurrentLightLayout(state);
    private void ValidateCardPower(HostState state, SubmitRequest request, StepSnapshot[] snapshots) => _devices.ValidateCardPower(state, request, snapshots);
    public DeviceConfig SaveDevice(string token, DeviceRequest request) => _devices.SaveDevice(token, request);
    public RoleBinding SaveRole(string token, RoleRequest request) => _devices.SaveRole(token, request);
    public bool UnassignRole(string token, UnassignRoleRequest request) => _devices.UnassignRole(token, request);
    public LightLayout SaveLightOrder(string token, LightOrderRequest request) => _devices.SaveLightOrder(token, request);
    public Task<DeviceState> ReconcileAsync(string token, ReconcileRequest request, CancellationToken ct = default) => _devices.ReconcileAsync(token, request, ct);
}
