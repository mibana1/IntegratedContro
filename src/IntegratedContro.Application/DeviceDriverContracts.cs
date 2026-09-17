using IntegratedContro.Core;

namespace IntegratedContro.Application;

public interface IDeviceDriver
{
    string Id { get; }
    string Version { get; }
    DeviceModel[] Models { get; }
    void ValidateConfiguration(DeviceConfig device);
    Task<DriverResult> ExecuteAsync(DeviceCommand command, CancellationToken cancellationToken);
    Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken cancellationToken);
}

// The execution service supplies a detached copy of the admitted target. Scheduling,
// preconditions, failure policy and scenario state are not part of a device command.
public sealed record DeviceCommand(DeviceConfig Target, DeviceOperation Operation, int Value, string Unit);

// Only device outcomes belong here; workflow states are owned by the execution service.
public enum DriverStatus { Simulated, Sent, Acknowledged, Observed, Failed, Unknown }

public sealed record DriverResult(DriverStatus Status, string Detail, IReadOnlyDictionary<DeviceOperation, int>? Values = null,
    DeviceEvidence Evidence = DeviceEvidence.Simulation);
public sealed record DriverReading(bool Available, IReadOnlyDictionary<DeviceOperation, int> Values, string Detail,
    DeviceEvidence Evidence = DeviceEvidence.Simulation);
