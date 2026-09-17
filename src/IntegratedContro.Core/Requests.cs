namespace IntegratedContro.Core;

public sealed record LoginRequest(string UserName, string Password, Guid PcId, string PcName);
public sealed record LeaseRequest(long Generation);
public sealed record CreateAccountRequest(long Generation, string Name, string Password, AccountRole Role,
    bool AllDevices = true, Guid[]? DeviceIds = null);
public sealed record UpdateAccountRequest(long Generation, Guid AccountId, bool Enabled, bool AllDevices, Guid[] DeviceIds);
public sealed record DeviceRequest(long Generation, Guid Id, Guid PcId, string PcName, string Name,
    string ConnectionId, string ModelId, bool Enabled = true, VirtualFault Fault = VirtualFault.None,
    int LatencyMs = 50, int ExpectedVersion = 0)
{
    public string? DriverId { get; init; }
    public DeviceConnection? Connection { get; init; }
    public Dictionary<string, string>? DriverOptions { get; init; }
}
public sealed record RoleRequest(long Generation, string Id, Guid DeviceId, int ExpectedVersion = 0);
public sealed record UnassignRoleRequest(long Generation, string Id, Guid DeviceId, int ExpectedVersion);
public sealed record ScenarioRequest(long Generation, Guid Id, string Name, ScenarioStep[] Steps, int ExpectedVersion = 0);
public sealed record DeleteScenarioRequest(long Generation, Guid Id, int ExpectedVersion);
public sealed record SubmitRequest(Guid RequestId, long Generation, string? RoleId,
    DeviceOperation Operation = DeviceOperation.Power, int Value = 1, Guid? ScenarioId = null,
    int DelayBeforeMs = 0, int TimeoutMs = 3000, int ExpiresAfterSeconds = 86400,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CardPowerExpectation? CardPower = null);
public sealed record CardPowerExpectation(Guid DeviceId, Guid PcId, int DeviceVersion, int RoleVersion, int Power, DateTimeOffset ObservedAt);
public sealed record LightPowerTarget(string RoleId, CardPowerExpectation Expected);
public sealed record LightBatchRequest(Guid RequestId, long Generation, int Value, int LayoutVersion,
    Guid? GroupId, LightPowerTarget[] Targets);
public sealed record LightOrderRequest(long Generation, int ExpectedVersion, Guid[] DeviceIds, LightGroup[]? Groups = null);
public sealed record JobActionRequest(long Generation, Guid JobId);
public sealed record ReconcileRequest(long Generation, Guid DeviceId);
public sealed record RecoveryApprovalRequest(Guid ReviewId);
public sealed record ApiError(string Code, string Message);
