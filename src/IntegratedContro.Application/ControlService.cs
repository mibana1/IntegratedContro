using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

/// <summary>Single execution authority. Persist a new aggregate before exposing any accepted mutation.
/// The host holds the store's exclusive ownership for this service's entire lifetime.</summary>
public sealed partial class ControlService : ICameraContentCatalog
{
    private readonly HostAuthority _host;
    private readonly ScenarioJobLifecycle _jobs;
    private readonly CameraService _cameras;
    private readonly DeviceExecutionService _devices;
    private int _dispatching;
    private object _gate => _host.Gate;
    private HostState _state => _host.State;
    private bool _storageFailed => _host.StorageFailed;
    private bool _stopping => _host.Stopping;
    private DateTimeOffset Now => _host.Now;
    public ControlService(IStateStore store, IPasswordHasher passwords, IDeviceDriver driver,
        TimeProvider? time = null, int heartbeatTimeoutSeconds = 15,
        IHiperwallReader? hiperwall = null, ICredentialStore? credentials = null,
        IMediaMtxClient? media = null, IMediaSecretStore? mediaSecrets = null)
        : this(store, passwords, new DeviceDriverRegistry(driver), time, heartbeatTimeoutSeconds, hiperwall, credentials, media, mediaSecrets) { }

    public ControlService(IStateStore store, IPasswordHasher passwords, DeviceDriverRegistry drivers,
        TimeProvider? time = null, int heartbeatTimeoutSeconds = 15,
        IHiperwallReader? hiperwall = null, ICredentialStore? credentials = null,
        IMediaMtxClient? media = null, IMediaSecretStore? mediaSecrets = null)
    {
        _host = new(store, passwords, time, heartbeatTimeoutSeconds);
        _jobs = new(_host);
        _devices = new(_host, drivers); _hiperwall = hiperwall; _credentials = credentials;
        _cameras = new(_host, media, mediaSecrets, this);
        RecoverStartup(); _cameras.RecoverCameras();
    }
    private static string Digest(string value) => AcceptedJobRules.Digest(value);
    private void Healthy() => _host.Healthy();
    private void Persist(HostState next) => _host.Persist(next);
    private T Change<T>(Func<HostState, T> action) => _host.Change(action);
    private void Audit(HostState s, Guid? user, string action, string detail) => _host.Audit(s, user, action, detail);
    private Session Authenticate(string token) => _host.Authenticate(token);
    private Account User(HostState s, Session session) => _host.User(s, session);
    private Session Owner(HostState s, string token, long generation) => _host.Owner(s, token, generation);
    private Session Admin(HostState s, string token) => _host.Admin(s, token);
    private static bool CanControl(Account user, Guid deviceId) => ControlAuthorization.CanControl(user, deviceId);
    private static bool HoldsReservations(Job job) => AcceptedJobRules.HoldsReservations(job);
    private void Fence(HostState s, string reason) => _host.Fence(s, reason);
    private void CheckConnectionUnsafe() => _host.CheckConnectionUnsafe();
    public void CheckConnections() => _host.CheckConnections();
    public LoginResult Login(LoginRequest request) => _host.Login(request);
    public StateView GetState(string token)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var session = Authenticate(token);
            var s = _state;
            var lease = _host.ReadLease();
            return JsonDefaults.Copy(new StateView(s.SiteId, s.SiteName, s.Revision, lease, session.Info,
                s.Devices.ToArray(), s.DeviceStates, s.Roles.ToArray(), s.Scenarios.ToArray(), s.Jobs.ToArray(),
                s.UncertainDevices.ToArray(), User(s, session).Role == AccountRole.Administrator
                    ? s.Accounts.Select(a => new AccountView(a.Id, a.Name, a.Role, a.Enabled, a.AllDevices, a.DeviceIds.ToArray())).ToArray() : [],
                s.Audit.TakeLast(200).Select(a => AuditPresentation.Enrich(a, s)).ToArray(), _devices.Models, _host.HeartbeatTimeoutSeconds)
                { HiperwallWriteSupported = _hiperwall is IHiperwallWriter, CanControlHiperwall = HiperwallPermission(User(s, session)), HiperwallReadSupported = _hiperwall is not null, HiperwallConfigurationVersion = s.Hiperwall?.Version ?? 0,
                    OutstandingHiperwallEdits = s.HiperwallEdits.Where(r => r.NeedsAttention).OrderByDescending(r => r.AcceptedAt).ToArray(),
                    OutstandingHiperwallDisplays = s.HiperwallDisplays.Where(j => j.Outstanding).ToArray(),
                    HiperwallDisplayJobs = s.HiperwallDisplays.Where(j => j.Outstanding).Union(s.HiperwallDisplays.TakeLast(100)).Reverse().ToArray(),
                    HiperwallLayoutsSupported = _hiperwall is IHiperwallWriter,
                    HiperwallSlotsSupported = _hiperwall is IHiperwallWriter, HiperwallSlots = s.HiperwallSlots.ToArray(),
                    ScenarioExtensionsSupported = true, ScenarioDeletionSupported = true, RoleUnassignmentSupported = true, SavedHiperwallLayouts = s.HiperwallLayouts.ToArray(),
                    CameraSupported = _cameras.Supported, MediaConfigurationVersion = s.Media?.Version ?? 0,
                    LightCardsSupported = true, LightGroupsSupported = true, LightBatchSupported = true, LightLayout = CurrentLightLayout(s),
                    ControllableDeviceIds = s.Devices.Where(d => CanControl(User(s, session), d.Id)).Select(d => d.Id).ToArray() });
        }
    }
    public Lease Acquire(string token) => _host.Acquire(token);
    public Lease Heartbeat(string token, long generation) => _host.Heartbeat(token, generation);
    public Lease Release(string token, long generation) => _host.Release(token, generation);
    public RecoveryReview ReviewRecovery(string token) => _host.ReviewRecovery(token);
    public Lease ApproveRecovery(string token, Guid reviewId) => _host.ApproveRecovery(token, reviewId);
    public bool Logout(string token)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var sessionId = Authenticate(token).Info.Id;
            var result = _host.Logout(token);
            CancelHiperwallSession(sessionId);
            return result;
        }
    }
    public void StopAccepting()
    {
        lock (_gate) { _host.StopAccepting(); MarkDisplaysForShutdown(); _cameras.Stop(); _previewStopping.Cancel(); foreach (var query in _hiperwallQueries.Values) query.Cancel(); }
    }
}
