using IntegratedContro.Core;

namespace IntegratedContro.Application;

// An opaque token: the recipient cannot cast it to the caller's feature state.
// Only tokens registered by this authority in the current synchronous scope are accepted.
internal sealed class StateContext { internal StateContext() { } }
internal abstract class FeatureStateScope(StateContext context)
{
    public static implicit operator StateContext(FeatureStateScope scope) => scope._context;
    private readonly StateContext _context = context;
}
internal sealed record SessionIdentity(SessionInfo Info);

// Open serializes a synchronous admission/dispatch section. It must never span an await.
// Current is detached; only a Draft can be committed. For joins the caller's transaction,
// projecting only this capability's state. No aggregate, raw lock or store escapes this port.
internal interface IFeatureStateAccess<TState> where TState : FeatureStateScope
{
    IDisposable Open();
    TState Current { get; }
    TState Draft();
    TState For(StateContext context);
    void Commit(TState draft);
    TResult Change<TResult>(Func<TState, TResult> action);
    DateTimeOffset Now { get; }
    bool StorageFailed { get; }
    bool Stopping { get; }
    void Healthy();
    void CheckConnections();
    SessionIdentity Authenticate(string token);
    SessionIdentity Owner(StateContext context, string token, long generation);
    SessionIdentity Admin(StateContext context, string token);
    void Audit(StateContext context, Guid? user, string action, string detail);
}

internal interface IDeviceStateAccess : IFeatureStateAccess<DeviceStateScope>
{
    AccountView User(StateContext context, SessionIdentity session);
    AccountView? FindAccount(StateContext context, Guid id);
}
internal abstract class DeviceStateScope : FeatureStateScope
{
    private protected DeviceStateScope(StateContext context) : base(context) { }
    public abstract List<DeviceConfig> Devices { get; set; }
    public abstract List<SharedDeviceConnection> Connections { get; set; }
    public abstract List<PcRegistration> Pcs { get; set; }
    public abstract Dictionary<Guid, DeviceState> DeviceStates { get; set; }
    public abstract List<RoleBinding> Roles { get; set; }
    public abstract List<RoleBinding> UnassignedRoles { get; set; }
    public abstract HashSet<string> RemovedRoleIds { get; set; }
    public abstract Dictionary<string, int> DeletedRoleVersions { get; set; }
    public abstract List<Guid> UncertainDevices { get; set; }
    public abstract LightLayout LightLayout { get; set; }
    public abstract List<LightSlot> LightSlots { get; set; }
    public abstract IReadOnlyList<Job> Jobs { get; }
    public abstract IReadOnlyList<ScenarioDefinition> Scenarios { get; }
}

internal interface IScenarioStateAccess : IFeatureStateAccess<ScenarioStateScope>
{
    AccountView User(StateContext context, SessionIdentity session);
}
internal abstract class ScenarioStateScope : FeatureStateScope
{
    private protected ScenarioStateScope(StateContext context) : base(context) { }
    public abstract Guid SiteId { get; }
    public abstract List<ScenarioDefinition> Scenarios { get; set; }
    public abstract Dictionary<Guid, int> DeletedScenarioVersions { get; set; }
    public abstract List<Job> Jobs { get; set; }
    public abstract IReadOnlyDictionary<Guid, DeviceState> DeviceStates { get; }
    public abstract IReadOnlyList<Guid> UncertainDevices { get; }
    public abstract LightLayout LightLayout { get; }
}

internal interface IHiperwallStateAccess : IFeatureStateAccess<HiperwallStateScope>
{
    AccountView User(StateContext context, SessionIdentity session);
    AccountView? FindAccount(StateContext context, Guid id);
    bool IsFenced(Guid sessionId);
}
internal abstract class HiperwallStateScope : FeatureStateScope
{
    private protected HiperwallStateScope(StateContext context) : base(context) { }
    public abstract Guid SiteId { get; }
    public abstract HiperwallConfiguration? Hiperwall { get; set; }
    public abstract List<HiperwallEditReceipt> HiperwallEdits { get; set; }
    public abstract List<HiperwallDisplayJob> HiperwallDisplays { get; set; }
    public abstract List<SavedHiperwallLayout> HiperwallLayouts { get; set; }
    public abstract List<HiperwallSlot> HiperwallSlots { get; set; }
    public abstract IReadOnlyList<Job> Jobs { get; }
    public abstract IReadOnlyList<ScenarioDefinition> Scenarios { get; }
}

internal interface ICameraStateAccess : IFeatureStateAccess<CameraStateScope> { }
internal abstract class CameraStateScope : FeatureStateScope
{
    private protected CameraStateScope(StateContext context) : base(context) { }
    public abstract Guid SiteId { get; }
    public abstract MediaConfiguration? Media { get; set; }
    public abstract LocalMediaChange? LocalMediaChange { get; set; }
    public abstract List<CameraRegistration> Cameras { get; set; }
    public abstract List<CameraCleanup> CameraCleanup { get; set; }
    public abstract List<Guid> MediaSecretsToDelete { get; set; }
}

internal sealed record RecoveryContribution(string Fingerprint, Func<RecoveryReview, RecoveryReview> Enrich);
