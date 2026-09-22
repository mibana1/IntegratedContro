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
public enum JobKind { Manual, Scenario, LightBatch, LightSlot }
public enum JobStatus { Queued, Running, StopRequested, Completed, Cancelled, Interrupted, NeedsReview }
public enum StepStatus { Pending, Dispatching, Simulated, Failed, Unknown, Skipped, Waiting, ConditionMet, Acknowledged, Sent, Observed }
public enum ScenarioStepKind { DeviceCommand, WaitUntil, DisplayLayout }

public sealed record Capability(DeviceOperation Operation, int Minimum, int Maximum, string Unit, bool CanRead = true);
public sealed record DeviceModel(string Id, string Name, Capability[] Capabilities, DeviceCategory Category = DeviceCategory.Other)
{
    public string DriverId { get; init; } = "virtual";
    public string DriverVersion { get; init; } = "1";
    public string[] TransportIds { get; init; } = ["virtual"];
    public bool IsSimulation { get; init; } = true;
    public bool RequiresTargetPc { get; init; }
    public string[] ExecutionPcTransportIds { get; init; } = [];
    public DeviceSettingDefinition[] Settings { get; init; } = [];
    public bool Equals(DeviceModel? other) => other is not null && Id == other.Id && Name == other.Name &&
        Category == other.Category && DriverId == other.DriverId && DriverVersion == other.DriverVersion && IsSimulation == other.IsSimulation &&
        Capabilities.SequenceEqual(other.Capabilities) && TransportIds.SequenceEqual(other.TransportIds) && RequiresTargetPc == other.RequiresTargetPc &&
        ExecutionPcTransportIds.SequenceEqual(other.ExecutionPcTransportIds) && JsonSerializer.Serialize(Settings, JsonDefaults.Options) == JsonSerializer.Serialize(other.Settings, JsonDefaults.Options);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Category, DriverId, IsSimulation);
}
public sealed record LightGroup(Guid Id, string Name, Guid[] DeviceIds);
public sealed record LightLayout(int Version, Guid[] DeviceIds)
{
    public LightGroup[] Groups { get; init; } = [];
}
public sealed record DeviceConfig(Guid Id, Guid PcId, string PcName, string Name, string ConnectionId,
    string ModelId, int Version, bool Enabled, VirtualFault Fault, int LatencyMs)
{
    // Legacy records without this field start from their saved edit version.
    public int ExecutionVersion { get; init; } = Version;
    public string DriverId { get; init; } = "virtual";
    public DeviceConnection Connection { get; init; } = new();
    public Dictionary<string, string> DriverOptions { get; init; } = [];
    public string Location { get; init; } = "";
    public string? LegacyConnectionId { get; init; }
    public bool HasSameExecutionSettings(DeviceConfig other) => Id == other.Id && PcId == other.PcId &&
        ConnectionId == other.ConnectionId && ModelId == other.ModelId && Enabled == other.Enabled &&
        Fault == other.Fault && LatencyMs == other.LatencyMs && DriverId == other.DriverId &&
        Connection.HasSameSettings(other.Connection) && DeviceConnection.SameOptions(DriverOptions, other.DriverOptions);
    public bool Equals(DeviceConfig? other) => other is not null && Version == other.Version &&
        ExecutionVersion == other.ExecutionVersion && Name == other.Name && PcName == other.PcName && Location == other.Location && HasSameExecutionSettings(other);
    public override int GetHashCode() => HashCode.Combine(Id, Version, ExecutionVersion, Name, PcName);
    public bool MatchesExecutionTarget(DeviceConfig other) => ExecutionVersion == other.ExecutionVersion &&
        HasSameExecutionSettings(other);
}
public sealed record RoleBinding(string Id, Guid DeviceId, int Version)
{
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
}
public sealed record StateValue(int Value, DateTimeOffset At, string Evidence = "가상 상태");
public sealed class DeviceState
{
    public Dictionary<DeviceOperation, int> Desired { get; set; } = [];
    public Dictionary<DeviceOperation, StateValue> Simulated { get; set; } = [];
    public Dictionary<DeviceOperation, StateValue> Observed { get; set; } = [];
    [JsonIgnore] public IReadOnlyDictionary<DeviceOperation, StateValue> Values => Observed.Count > 0 ? Observed : Simulated;
    public string Connection { get; set; } = "아직 조회하지 않음";
    public string LastResult { get; set; } = "없음";
}
public sealed record ScenarioStep(string RoleId, DeviceOperation Operation, int Value,
    int DelayBeforeMs = 0, int TimeoutMs = 3000, FailurePolicy OnFailure = FailurePolicy.Stop,
    DeviceOperation? ConditionOperation = null, int? ConditionValue = null,
    ScenarioStepKind Kind = ScenarioStepKind.DeviceCommand, Guid? LayoutId = null)
{
    public string KindLabel => Kind switch { ScenarioStepKind.WaitUntil => "조건 충족까지 대기", ScenarioStepKind.DisplayLayout => "저장 배치 표시", _ => "장비 명령" };
    [JsonIgnore] public string ActionLabel => Kind == ScenarioStepKind.DisplayLayout ? "표시 응답 대기" : DeviceLabels.Format(Operation, Value);
    [JsonIgnore] public string TargetLabel => Kind == ScenarioStepKind.DisplayLayout ? LayoutId?.ToString() ?? "배치 미선택" : RoleId;
}
public sealed record ScenarioDefinition(Guid Id, string Name, int Version, ScenarioStep[] Steps);
public sealed record ScenarioDisplaySnapshot(SavedHiperwallLayout Layout, string Endpoint, Guid RequestId);
public sealed record StepSnapshot(RoleBinding? Role, DeviceConfig? Target, DeviceOperation Operation,
    int Value, string Unit, int DelayBeforeMs, int TimeoutMs, FailurePolicy OnFailure,
    DeviceOperation? ConditionOperation, int? ConditionValue)
{
    public ScenarioStepKind Kind { get; init; }
    public ScenarioDisplaySnapshot? Display { get; init; }
    public DeviceModel? ModelDefinition { get; init; }
    [JsonIgnore] public string TargetLabel => Display is { } d ? $"{d.Layout.Name} / {d.Endpoint}" : $"{Target?.Name}" + (ModelDefinition?.RequiresTargetPc == true ? $" ({Target?.PcName})" : "");
    [JsonIgnore] public string KindLabel => Kind switch { ScenarioStepKind.WaitUntil => "조건 충족까지 대기", ScenarioStepKind.DisplayLayout => "저장 배치 표시", _ => "장비 명령" };
}
public sealed record ExecutionSnapshot(Guid SiteId, string Mode, Guid RequestId, Guid RequestedBy,
    string RequesterName, Guid SessionId, Guid ClientPcId, string ClientPcName, long LeaseGeneration,
    DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt, Guid? ScenarioId, int? ScenarioVersion,
    string Name, StepSnapshot[] Steps);
public sealed class StepRun
{
    public StepStatus Status { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Result { get; set; } = "미전송";
}
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RequestFingerprint { get; set; }
    public required ExecutionSnapshot Snapshot { get; set; }
    public JobKind Kind { get; set; }
    [JsonIgnore] public bool IsLightBatch => Kind == JobKind.LightBatch;
    [JsonIgnore] public bool IsDeviceBatch => Kind is JobKind.LightBatch or JobKind.LightSlot;
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
public sealed record AuditEntry(DateTimeOffset At, Guid? UserId, string Action, string Detail)
{
    public string? UserName { get; init; }
    public string? EventName { get; init; }
    public string? Message { get; init; }
}
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
    public int DeviceConfigurationVersion { get; set; }
    public List<SharedDeviceConnection> DeviceConnections { get; set; } = [];
    public List<PcRegistration> Pcs { get; set; } = [];
    public HiperwallConfiguration? Hiperwall { get; set; }
    public MediaConfiguration? Media { get; set; }
    public LocalMediaChange? LocalMediaChange { get; set; }
    public List<CameraRegistration> Cameras { get; set; } = [];
    public List<CameraCleanup> CameraCleanup { get; set; } = [];
    public List<Guid> MediaSecretsToDelete { get; set; } = [];
    public List<HiperwallEditReceipt> HiperwallEdits { get; set; } = [];
    public List<SavedHiperwallLayout> HiperwallLayouts { get; set; } = [];
    public List<HiperwallDisplayJob> HiperwallDisplays { get; set; } = [];
    public LightLayout LightLayout { get; set; } = new(0, []);
    public List<LightSlot> LightSlots { get; set; } = [];
    public Dictionary<Guid, DeviceState> DeviceStates { get; set; } = [];
    public List<RoleBinding> Roles { get; set; } = [];
    public List<RoleBinding> UnassignedRoles { get; set; } = [];
    public HashSet<string> RemovedRoleIds { get; set; } = [];
    // Keep removed versions so recreating an ID cannot validate an old request again.
    public Dictionary<string, int> DeletedRoleVersions { get; set; } = [];
    public List<HiperwallSlot> HiperwallSlots { get; set; } = [];
    public List<ScenarioDefinition> Scenarios { get; set; } = [];
    public Dictionary<Guid, int> DeletedScenarioVersions { get; set; } = [];
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
    public bool HiperwallReadSupported { get; init; }
    public bool CameraSupported { get; init; }
    public int MediaConfigurationVersion { get; init; }
    public bool HiperwallWriteSupported { get; init; }
    public bool CanControlHiperwall { get; init; }
    public int HiperwallConfigurationVersion { get; init; }
    public HiperwallEditReceipt[] OutstandingHiperwallEdits { get; init; } = [];
    public HiperwallDisplayJob[] OutstandingHiperwallDisplays { get; init; } = [];
    public HiperwallDisplayJob[] HiperwallDisplayJobs { get; init; } = [];
    public bool HiperwallLayoutsSupported { get; init; }
    public bool HiperwallSlotsSupported { get; init; }
    public HiperwallSlot[] HiperwallSlots { get; init; } = [];
    public bool ScenarioExtensionsSupported { get; init; }
    public bool ScenarioDeletionSupported { get; init; }
    public bool RoleUnassignmentSupported { get; init; }
    public bool RoleManagementSupported { get; init; }
    public bool UnassignedRoleCreationSupported { get; init; }
    public SavedHiperwallLayout[] SavedHiperwallLayouts { get; init; } = [];
    public bool LightGroupsSupported { get; init; }
    public bool LightBatchSupported { get; init; }
    public bool LightSlotsSupported { get; init; }
    public LightSlot[] LightSlots { get; init; } = [];
    public Guid[] ControllableDeviceIds { get; init; } = [];
    public RoleBinding[] UnassignedRoles { get; init; } = [];
    public bool DeviceConfigurationSupported { get; init; }
    public SharedDeviceConnection[] DeviceConnections { get; init; } = [];
    public PcRegistration[] Pcs { get; init; } = [];
}
public sealed record RecoveryReview(Guid ReviewId, long Generation, DateTimeOffset ReviewedAt, Job[] Jobs, Guid[] UncertainDevices)
{
    public HiperwallEditReceipt[] HiperwallEdits { get; init; } = [];
    public HiperwallDisplayJob[] HiperwallDisplays { get; init; } = [];
}
public sealed class DomainException(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
