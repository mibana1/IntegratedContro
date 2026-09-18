using IntegratedContro.Core;
using static IntegratedContro.Application.ControlAuthorization;

namespace IntegratedContro.Application;

/// <summary>Stable host API and composition. Domain algorithms belong to the four services.</summary>
public sealed partial class ControlService
{
    private readonly HostAuthority _host;
    private readonly DeviceExecutionService _devices;
    private readonly ScenarioService _scenarios;
    private readonly CameraService _cameras;
    private readonly HiperwallService _wall;
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
        var jobs = new ScenarioJobLifecycle(_host.ScenarioAccess(), new HiperwallJobLifecycle(_host.HiperwallAccess()));
        _devices = new(_host.DeviceAccess(), drivers);
        _wall = new(_host.HiperwallAccess(), hiperwall, credentials, jobs);
        _scenarios = new(_host.ScenarioAccess(), _devices, _wall, jobs);
        _cameras = new(_host.CameraAccess(), media, mediaSecrets, _wall);
        RecoverStartup();
    }
    private void RecoverStartup() => _host.RecoverStartup(context =>
    {
        _wall.RecoverEdits(context);
        _scenarios.RecoverJobs(context);
        _wall.RecoverHiperwallDisplays(context);
        _cameras.RecoverCameras(context);
    });
    public StateView GetState(string token)
    {
        using (_host.Open())
        {
            _host.Healthy(); _host.CheckConnectionUnsafe();
            var session = _host.Authenticate(token);
            var s = _host.Snapshot;
            var lease = _host.ReadLease();
            return JsonDefaults.Copy(new StateView(s.SiteId, s.SiteName, s.Revision, lease, session.Info,
                s.Devices.ToArray(), s.DeviceStates, s.Roles.ToArray(), s.Scenarios.ToArray(), s.Jobs.ToArray(),
                s.UncertainDevices.ToArray(), _host.User(s, session).Role == AccountRole.Administrator
                    ? s.Accounts.Select(a => new AccountView(a.Id, a.Name, a.Role, a.Enabled, a.AllDevices, a.DeviceIds.ToArray())).ToArray() : [],
                s.Audit.TakeLast(200).Select(a => AuditPresentation.Enrich(a, s)).ToArray(), _devices.Models, _host.HeartbeatTimeoutSeconds)
                { UnassignedRoles = s.UnassignedRoles.ToArray(), DeviceConfigurationSupported = true, DeviceConnections = s.DeviceConnections.ToArray(), Pcs = s.Pcs.ToArray(), HiperwallWriteSupported = _wall.WriteSupported, CanControlHiperwall = HiperwallPermission(_host.User(s, session)), HiperwallReadSupported = _wall.ReadSupported, HiperwallConfigurationVersion = s.Hiperwall?.Version ?? 0,
                    OutstandingHiperwallEdits = s.HiperwallEdits.Where(r => r.NeedsAttention).OrderByDescending(r => r.AcceptedAt).ToArray(),
                    OutstandingHiperwallDisplays = s.HiperwallDisplays.Where(j => j.Outstanding).ToArray(),
                    HiperwallDisplayJobs = s.HiperwallDisplays.Where(j => j.Outstanding).Union(s.HiperwallDisplays.TakeLast(100)).Reverse().ToArray(),
                    HiperwallLayoutsSupported = _wall.WriteSupported,
                    HiperwallSlotsSupported = _wall.WriteSupported, HiperwallSlots = s.HiperwallSlots.ToArray(),
                    ScenarioExtensionsSupported = true, ScenarioDeletionSupported = true, RoleUnassignmentSupported = true, RoleManagementSupported = true,
                    UnassignedRoleCreationSupported = true, SavedHiperwallLayouts = s.HiperwallLayouts.ToArray(),
                    CameraSupported = _cameras.Supported, MediaConfigurationVersion = s.Media?.Version ?? 0,
                    LightSlotsSupported = true, LightSlots = s.LightSlots.ToArray(),
                    LightCardsSupported = true, LightGroupsSupported = true, LightBatchSupported = true, LightLayout = _devices.CurrentLightLayout(_host.ReadContext),
                    ControllableDeviceIds = s.Devices.Where(d => CanControl(_host.User(s, session), d.Id)).Select(d => d.Id).ToArray() });
        }
    }
    public bool Logout(string token)
    {
        using (_host.Open())
        {
            _host.Healthy(); _host.CheckConnectionUnsafe();
            var sessionId = _host.Authenticate(token).Info.Id;
            var result = _host.Logout(token);
            _wall.CancelHiperwallSession(sessionId);
            return result;
        }
    }
    public void StopAccepting()
    {
        using (_host.Open()) { _host.StopAccepting(); _wall.Stop(); _cameras.Stop(); }
    }
    public void CheckConnections() => _host.CheckConnections();
    public LoginResult Login(LoginRequest request) => _host.Login(request);
    public Lease Acquire(string token) => _host.Acquire(token);
    public Lease Heartbeat(string token, long generation) => _host.Heartbeat(token, generation);
    public Lease Release(string token, long generation) => _host.Release(token, generation);
    public RecoveryReview ReviewRecovery(string token) => _host.ReviewRecovery(token, _wall.DescribeRecovery);
    public Lease ApproveRecovery(string token, Guid reviewId) => _host.ApproveRecovery(token, reviewId, _wall.DescribeRecovery);
}
