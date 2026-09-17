using IntegratedContro.Core;
namespace IntegratedContro.Infrastructure;
/// <summary>Simulation persistence boundary. Physical stream/HTTP drivers use their own transport contracts.</summary>
public interface IVirtualDeviceTransport
{
    Task WriteAsync(DeviceConfig target, DeviceOperation operation, int value, CancellationToken ct);
    Task<IReadOnlyDictionary<DeviceOperation, int>> ReadAsync(DeviceConfig target, CancellationToken ct);
}

