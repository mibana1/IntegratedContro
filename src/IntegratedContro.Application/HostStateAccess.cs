using System.Runtime.CompilerServices;
using IntegratedContro.Core;

namespace IntegratedContro.Application;

internal sealed partial class HostAuthority
{
    private readonly ConditionalWeakTable<StateContext, StateFrame> _contexts = new();
    private StateFrame? _readFrame;
    private object? _scopeLifetime;
    private int _scopeDepth;
    private sealed class StateFrame(HostState state, HostState original, object lifetime, bool writable)
    {
        public HostState State { get; } = state;
        public HostState Original { get; } = original;
        public object Lifetime { get; } = lifetime;
        public bool Writable { get; } = writable;
        public bool Committed { get; set; }
        public StateContext Context { get; } = new();
        public Dictionary<Type, FeatureStateScope> Projections { get; } = [];
    }
    private sealed class Scope(HostAuthority host) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            host.RequireScope();
            _disposed = true;
            if (--host._scopeDepth == 0) { host._readFrame = null; host._scopeLifetime = null; }
            Monitor.Exit(host._gate);
        }
    }
    internal IDisposable Open()
    {
        Monitor.Enter(_gate);
        if (_scopeDepth++ == 0) _scopeLifetime = new object();
        return new Scope(this);
    }
    private void RequireScope()
    {
        if (!Monitor.IsEntered(_gate) || _scopeDepth == 0)
            throw new InvalidOperationException("State access requires an open synchronous scope.");
    }
    private StateFrame Frame(StateContext context)
    {
        RequireScope();
        if (!_contexts.TryGetValue(context, out var frame) || frame.Lifetime != _scopeLifetime || frame.Committed ||
            !ReferenceEquals(frame.Original, _state))
            throw new InvalidOperationException("The state context belongs to another host, scope or revision.");
        return frame;
    }
    private StateFrame NewFrame(bool writable)
    {
        RequireScope();
        var frame = new StateFrame(JsonDefaults.Copy(_state), _state, _scopeLifetime!, writable);
        _contexts.Add(frame.Context, frame);
        return frame;
    }
    private StateFrame ReadFrame()
    {
        RequireScope();
        if (_readFrame is null || !ReferenceEquals(_readFrame.Original, _state)) _readFrame = NewFrame(false);
        return _readFrame;
    }
    private T Project<T>(StateFrame frame, Func<HostState, StateContext, T> create) where T : FeatureStateScope
    {
        if (!frame.Projections.TryGetValue(typeof(T), out var value))
        {
            value = create(frame.State, frame.Context);
            frame.Projections.Add(typeof(T), value);
        }
        return (T)value;
    }
    private void Commit(StateContext context)
    {
        var frame = Frame(context);
        if (!frame.Writable) throw new InvalidOperationException("A read snapshot cannot be committed.");
        if (_storageFailed) throw new DomainException("host_unavailable", "호스트 저장 상태를 확인하세요.", 503);
        // Never install an object graph still held by a service into the authoritative state.
        Persist(JsonDefaults.Copy(frame.State));
        frame.Committed = true;
    }
    private static AccountView AccountSnapshot(Account a) => new(a.Id, a.Name, a.Role, a.Enabled, a.AllDevices, a.DeviceIds.ToArray());
    private abstract class FeatureAccess<T>(HostAuthority host, Func<HostState, StateContext, T> create) : IFeatureStateAccess<T> where T : FeatureStateScope
    {
        public IDisposable Open() => host.Open();
        public T Current => host.Project(host.ReadFrame(), create);
        public T Draft() => host.Project(host.NewFrame(true), create);
        public T For(StateContext context) => host.Project(host.Frame(context), create);
        public void Commit(T draft) => host.Commit(draft);
        public TResult Change<TResult>(Func<T, TResult> action)
        {
            using (Open())
            {
                Healthy(); CheckConnections();
                var draft = Draft(); var result = action(draft);
                Commit(draft);
                return JsonDefaults.Copy(result);
            }
        }
        public DateTimeOffset Now => host.Now;
        public bool StorageFailed => host._storageFailed;
        public bool Stopping => host._stopping;
        public void Healthy() => host.Healthy();
        public void CheckConnections() { host.RequireScope(); host.CheckConnectionUnsafe(); }
        public SessionIdentity Authenticate(string token) { host.RequireScope(); return new(host.Authenticate(token).Info); }
        public SessionIdentity Owner(StateContext context, string token, long generation) => new(host.Owner(host.Frame(context).State, token, generation).Info);
        public SessionIdentity Admin(StateContext context, string token) => new(host.Admin(host.Frame(context).State, token).Info);
        public AccountView User(StateContext context, SessionIdentity session) => FindAccount(context, session.Info.UserId)!;
        public AccountView? FindAccount(StateContext context, Guid id) => host.Frame(context).State.Accounts.SingleOrDefault(a => a.Id == id) is { } a ? AccountSnapshot(a) : null;
        public bool IsFenced(Guid id) { host.RequireScope(); return host._state.FencedSessions.Contains(id); }
        public void Audit(StateContext context, Guid? user, string action, string detail)
        {
            var frame = host.Frame(context);
            if (!frame.Writable) throw new InvalidOperationException("An audit requires a change transaction.");
            host.Audit(frame.State, user, action, detail);
        }
    }
    internal IDeviceStateAccess DeviceAccess() => new DeviceAccessPort(this);
    private sealed class DeviceAccessPort(HostAuthority host) : FeatureAccess<DeviceStateScope>(host, (s, context) => new DeviceProjection(s, context)), IDeviceStateAccess;
    private sealed class DeviceProjection(HostState state, StateContext context) : DeviceStateScope(context)
    {
        public override List<DeviceConfig> Devices { get => state.Devices; set => state.Devices = value; }
        public override List<SharedDeviceConnection> Connections { get => state.DeviceConnections; set => state.DeviceConnections = value; }
        public override List<PcRegistration> Pcs { get => state.Pcs; set => state.Pcs = value; }
        public override Dictionary<Guid, DeviceState> DeviceStates { get => state.DeviceStates; set => state.DeviceStates = value; }
        public override List<RoleBinding> Roles { get => state.Roles; set => state.Roles = value; }
        public override List<RoleBinding> UnassignedRoles { get => state.UnassignedRoles; set => state.UnassignedRoles = value; }
        public override HashSet<string> RemovedRoleIds { get => state.RemovedRoleIds; set => state.RemovedRoleIds = value; }
        public override Dictionary<string, int> DeletedRoleVersions { get => state.DeletedRoleVersions; set => state.DeletedRoleVersions = value; }
        public override List<Guid> UncertainDevices { get => state.UncertainDevices; set => state.UncertainDevices = value; }
        public override LightLayout LightLayout { get => state.LightLayout; set => state.LightLayout = value; }
        public override IReadOnlyList<Job> Jobs => JsonDefaults.Copy(state.Jobs).AsReadOnly();
        public override IReadOnlyList<ScenarioDefinition> Scenarios => JsonDefaults.Copy(state.Scenarios).AsReadOnly();
    }
    internal IScenarioStateAccess ScenarioAccess() => new ScenarioAccessPort(this);
    private sealed class ScenarioAccessPort(HostAuthority host) : FeatureAccess<ScenarioStateScope>(host, (s, context) => new ScenarioProjection(s, context)), IScenarioStateAccess;
    private sealed class ScenarioProjection(HostState state, StateContext context) : ScenarioStateScope(context)
    {
        public override Guid SiteId => state.SiteId;
        public override List<ScenarioDefinition> Scenarios { get => state.Scenarios; set => state.Scenarios = value; }
        public override Dictionary<Guid, int> DeletedScenarioVersions { get => state.DeletedScenarioVersions; set => state.DeletedScenarioVersions = value; }
        public override List<Job> Jobs { get => state.Jobs; set => state.Jobs = value; }
        public override IReadOnlyDictionary<Guid, DeviceState> DeviceStates => new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, DeviceState>(JsonDefaults.Copy(state.DeviceStates));
        public override IReadOnlyList<Guid> UncertainDevices => JsonDefaults.Copy(state.UncertainDevices).AsReadOnly();
        public override LightLayout LightLayout => JsonDefaults.Copy(state.LightLayout);
    }
    internal IHiperwallStateAccess HiperwallAccess() => new HiperwallAccessPort(this);
    private sealed class HiperwallAccessPort(HostAuthority host) : FeatureAccess<HiperwallStateScope>(host, (s, context) => new HiperwallProjection(s, context)), IHiperwallStateAccess;
    private sealed class HiperwallProjection(HostState state, StateContext context) : HiperwallStateScope(context)
    {
        public override Guid SiteId => state.SiteId;
        public override HiperwallConfiguration? Hiperwall { get => state.Hiperwall; set => state.Hiperwall = value; }
        public override List<HiperwallEditReceipt> HiperwallEdits { get => state.HiperwallEdits; set => state.HiperwallEdits = value; }
        public override List<HiperwallDisplayJob> HiperwallDisplays { get => state.HiperwallDisplays; set => state.HiperwallDisplays = value; }
        public override List<SavedHiperwallLayout> HiperwallLayouts { get => state.HiperwallLayouts; set => state.HiperwallLayouts = value; }
        public override List<HiperwallSlot> HiperwallSlots { get => state.HiperwallSlots; set => state.HiperwallSlots = value; }
        public override IReadOnlyList<Job> Jobs => JsonDefaults.Copy(state.Jobs).AsReadOnly();
        public override IReadOnlyList<ScenarioDefinition> Scenarios => JsonDefaults.Copy(state.Scenarios).AsReadOnly();
    }
    internal ICameraStateAccess CameraAccess() => new CameraAccessPort(this);
    private sealed class CameraAccessPort(HostAuthority host) : FeatureAccess<CameraStateScope>(host, (s, context) => new CameraProjection(s, context)), ICameraStateAccess;
    private sealed class CameraProjection(HostState state, StateContext context) : CameraStateScope(context)
    {
        public override Guid SiteId => state.SiteId;
        public override MediaConfiguration? Media { get => state.Media; set => state.Media = value; }
        public override List<CameraRegistration> Cameras { get => state.Cameras; set => state.Cameras = value; }
        public override List<CameraCleanup> CameraCleanup { get => state.CameraCleanup; set => state.CameraCleanup = value; }
        public override List<Guid> MediaSecretsToDelete { get => state.MediaSecretsToDelete; set => state.MediaSecretsToDelete = value; }
    }
    internal StateContext ReadContext => ReadFrame().Context;
    internal void RecoverStartup(Action<StateContext> recover)
    {
        using (Open())
        {
            var frame = NewFrame(true);
            if (frame.State.Lease.Mode != LeaseMode.Free) Fence(frame.State, "호스트 재시작: 이전 인증 세션 무효");
            recover(frame.Context);
            Audit(frame.State, null, "HostStarted", "전송 중 명령 대조 및 중단 시나리오 자동 재개 차단");
            Commit(frame.Context);
        }
    }
}
