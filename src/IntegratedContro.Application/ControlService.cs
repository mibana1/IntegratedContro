using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

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
        var jobs = new ScenarioJobLifecycle(_host);
        _devices = new(_host, drivers);
        _wall = new(_host, hiperwall, credentials, jobs);
        _scenarios = new(_host, _devices, _wall, jobs);
        _cameras = new(_host, media, mediaSecrets, _wall);
        RecoverStartup();
    }
    private void RecoverStartup()
    {
        lock (_host.Gate)
        {
            var next = JsonDefaults.Copy(_host.State);
            if (next.Lease.Mode != LeaseMode.Free) _host.Fence(next, "호스트 재시작: 이전 인증 세션 무효");
            _wall.RecoverEdits(next);
            _scenarios.RecoverJobs(next);
            _wall.RecoverHiperwallDisplays(next);
            _host.Audit(next, null, "HostStarted", "전송 중 명령 대조 및 중단 시나리오 자동 재개 차단");
            _host.Persist(next);
            _cameras.RecoverCameras();
        }
    }
    public StateView GetState(string token)
    {
        lock (_host.Gate)
        {
            _host.Healthy(); _host.CheckConnectionUnsafe();
            var session = _host.Authenticate(token);
            var s = _host.State;
            var lease = _host.ReadLease();
            return JsonDefaults.Copy(new StateView(s.SiteId, s.SiteName, s.Revision, lease, session.Info,
                s.Devices.ToArray(), s.DeviceStates, s.Roles.ToArray(), s.Scenarios.ToArray(), s.Jobs.ToArray(),
                s.UncertainDevices.ToArray(), _host.User(s, session).Role == AccountRole.Administrator
                    ? s.Accounts.Select(a => new AccountView(a.Id, a.Name, a.Role, a.Enabled, a.AllDevices, a.DeviceIds.ToArray())).ToArray() : [],
                s.Audit.TakeLast(200).Select(a => AuditPresentation.Enrich(a, s)).ToArray(), _devices.Models, _host.HeartbeatTimeoutSeconds)
                { HiperwallWriteSupported = _wall.WriteSupported, CanControlHiperwall = HiperwallPermission(_host.User(s, session)), HiperwallReadSupported = _wall.ReadSupported, HiperwallConfigurationVersion = s.Hiperwall?.Version ?? 0,
                    OutstandingHiperwallEdits = s.HiperwallEdits.Where(r => r.NeedsAttention).OrderByDescending(r => r.AcceptedAt).ToArray(),
                    OutstandingHiperwallDisplays = s.HiperwallDisplays.Where(j => j.Outstanding).ToArray(),
                    HiperwallDisplayJobs = s.HiperwallDisplays.Where(j => j.Outstanding).Union(s.HiperwallDisplays.TakeLast(100)).Reverse().ToArray(),
                    HiperwallLayoutsSupported = _wall.WriteSupported,
                    HiperwallSlotsSupported = _wall.WriteSupported, HiperwallSlots = s.HiperwallSlots.ToArray(),
                    ScenarioExtensionsSupported = true, ScenarioDeletionSupported = true, RoleUnassignmentSupported = true, SavedHiperwallLayouts = s.HiperwallLayouts.ToArray(),
                    CameraSupported = _cameras.Supported, MediaConfigurationVersion = s.Media?.Version ?? 0,
                    LightCardsSupported = true, LightGroupsSupported = true, LightBatchSupported = true, LightLayout = _devices.CurrentLightLayout(s),
                    ControllableDeviceIds = s.Devices.Where(d => CanControl(_host.User(s, session), d.Id)).Select(d => d.Id).ToArray() });
        }
    }
    public bool Logout(string token)
    {
        lock (_host.Gate)
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
        lock (_host.Gate) { _host.StopAccepting(); _wall.Stop(); _cameras.Stop(); }
    }
    public void CheckConnections() => _host.CheckConnections();
    public LoginResult Login(LoginRequest request) => _host.Login(request);
    public Lease Acquire(string token) => _host.Acquire(token);
    public Lease Heartbeat(string token, long generation) => _host.Heartbeat(token, generation);
    public Lease Release(string token, long generation) => _host.Release(token, generation);
    public RecoveryReview ReviewRecovery(string token) => _host.ReviewRecovery(token);
    public Lease ApproveRecovery(string token, Guid reviewId) => _host.ApproveRecovery(token, reviewId);
}
