using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntegratedContro.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
public enum DeviceCategory { Other, Lighting, Projection, Audio, Lift }
public enum AccountRole { Administrator, Operator, Viewer }
public enum LeaseMode { Free, Held, RecoveryRequired }
public enum DeviceOperation { Power, Brightness, Volume, Mute, Input, Lift, Stop }
public enum VirtualFault { None, Failure, Disconnected, NoResponse, ResponseLost }
public enum FailurePolicy { Stop, Continue }
public enum JobKind { Manual, Scenario }
public enum JobStatus { Queued, Running, StopRequested, Completed, Cancelled, Interrupted, NeedsReview }
public enum StepStatus { Pending, Dispatching, Simulated, Failed, Unknown, Skipped }

public sealed record Capability(DeviceOperation Operation, int Minimum, int Maximum, string Unit);
public sealed record DeviceModel(string Id, string Name, Capability[] Capabilities, DeviceCategory Category = DeviceCategory.Other);
public sealed record LightLayout(int Version, Guid[] DeviceIds);
public sealed record DeviceConfig(Guid Id, Guid PcId, string PcName, string Name, string ConnectionId,
    string ModelId, int Version, bool Enabled, VirtualFault Fault, int LatencyMs);
public sealed record RoleBinding(string Id, Guid DeviceId, int Version);
public sealed record StateValue(int Value, DateTimeOffset At, string Evidence = "가상 상태");
public sealed class DeviceState
{
    public Dictionary<DeviceOperation, int> Desired { get; set; } = [];
    public Dictionary<DeviceOperation, StateValue> Simulated { get; set; } = [];
    public string Connection { get; set; } = "가상 / 아직 조회하지 않음";
    public string LastResult { get; set; } = "없음";
}
public sealed record ScenarioStep(string RoleId, DeviceOperation Operation, int Value,
    int DelayBeforeMs = 0, int TimeoutMs = 3000, FailurePolicy OnFailure = FailurePolicy.Stop,
    DeviceOperation? ConditionOperation = null, int? ConditionValue = null);
public sealed record ScenarioDefinition(Guid Id, string Name, int Version, ScenarioStep[] Steps);
public sealed record StepSnapshot(RoleBinding Role, DeviceConfig Target, DeviceOperation Operation,
    int Value, string Unit, int DelayBeforeMs, int TimeoutMs, FailurePolicy OnFailure,
    DeviceOperation? ConditionOperation, int? ConditionValue);
public sealed record ExecutionSnapshot(Guid SiteId, string Mode, Guid RequestId, Guid RequestedBy,
    string RequesterName, Guid SessionId, Guid ClientPcId, string ClientPcName, long LeaseGeneration,
    DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt, Guid? ScenarioId, int? ScenarioVersion,
    string Name, StepSnapshot[] Steps);
public sealed class StepRun
{
    public StepStatus Status { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Result { get; set; } = "미전송";
}
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RequestFingerprint { get; set; }
    public required ExecutionSnapshot Snapshot { get; set; }
    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public List<StepRun> Steps { get; set; } = [];
    public DateTimeOffset ReadyAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancellerName { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public string Result { get; set; } = "접수 완료";
    [JsonIgnore] public bool Active => Status is JobStatus.Queued or JobStatus.Running or JobStatus.StopRequested;
}
public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string PasswordHash { get; set; }
    public AccountRole Role { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllDevices { get; set; } = true;
    public List<Guid> DeviceIds { get; set; } = [];
}
public sealed record AccountView(Guid Id, string Name, AccountRole Role, bool Enabled, bool AllDevices, Guid[] DeviceIds);
public sealed class Lease
{
    public LeaseMode Mode { get; set; }
    public long Generation { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string? PcName { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LostAt { get; set; }
    public DateTimeOffset? FencedAt { get; set; }
    public string? RecoveryReason { get; set; }
}
public sealed record AuditEntry(DateTimeOffset At, Guid? UserId, string Action, string Detail);
public sealed class HostState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SiteId { get; set; } = Guid.NewGuid();
    public string SiteName { get; set; } = "";
    public bool Initialized { get; set; }
    public long Revision { get; set; }
    public Lease Lease { get; set; } = new();
    public List<Guid> FencedSessions { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
    public List<DeviceConfig> Devices { get; set; } = [];
    public LightLayout LightLayout { get; set; } = new(0, []);
    public Dictionary<Guid, DeviceState> DeviceStates { get; set; } = [];
    public List<RoleBinding> Roles { get; set; } = [];
    public List<ScenarioDefinition> Scenarios { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public List<Guid> UncertainDevices { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
}
public sealed record SessionInfo(Guid Id, Guid UserId, string UserName, AccountRole Role, Guid PcId, string PcName);
public sealed record LoginResult(string Token, SessionInfo Session);
public sealed record StateView(Guid SiteId, string SiteName, long Revision, Lease Lease, SessionInfo Session,
    DeviceConfig[] Devices, Dictionary<Guid, DeviceState> DeviceStates, RoleBinding[] Roles,
    ScenarioDefinition[] Scenarios, Job[] Jobs, Guid[] UncertainDevices, AccountView[] Accounts,
    AuditEntry[] Audit, DeviceModel[] Models, int HeartbeatTimeoutSeconds)
{
    public LightLayout LightLayout { get; init; } = new(0, []);
    public bool LightCardsSupported { get; init; }
    public Guid[] ControllableDeviceIds { get; init; } = [];
}
public sealed record RecoveryReview(Guid ReviewId, long Generation, DateTimeOffset ReviewedAt, Job[] Jobs, Guid[] UncertainDevices);
public sealed class DomainException(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
