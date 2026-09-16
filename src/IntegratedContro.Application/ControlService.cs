using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

/// <summary>Single execution authority. Persist a new aggregate before exposing any accepted mutation.
/// The host holds the store's exclusive ownership for this service's entire lifetime.</summary>
public sealed partial class ControlService
{
    private readonly object _gate = new();
    private readonly IStateStore _store;
    private readonly IPasswordHasher _passwords;
    private readonly IDeviceDriver _driver;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<Guid, ReviewTicket> _reviews = [];
    private HostState _state;
    private bool _storageFailed;
    private bool _stopping;
    private int _dispatching;
    private readonly TimeSpan _heartbeatTimeout;
    private DateTimeOffset Now => _time.GetUtcNow();
    private sealed record Session(SessionInfo Info, DateTimeOffset CreatedAt)
    { public DateTimeOffset LastSeen { get; set; } = CreatedAt; }
    private sealed record ReviewTicket(Guid UserId, long Generation, DateTimeOffset At, string Fingerprint);
    public ControlService(IStateStore store, IPasswordHasher passwords, IDeviceDriver driver,
        TimeProvider? time = null, int heartbeatTimeoutSeconds = 15,
        IHiperwallReader? hiperwall = null, ICredentialStore? credentials = null,
        IMediaMtxClient? media = null, IMediaSecretStore? mediaSecrets = null)
    {
        Require(heartbeatTimeoutSeconds is >= 3 and <= 300, "invalid_timeout", "생존 확인 제한은 3~300초입니다.", 400);
        _store = store; _passwords = passwords; _driver = driver;
        _hiperwall = hiperwall; _credentials = credentials;
        _media = media; _mediaSecrets = mediaSecrets;
        _time = time ?? TimeProvider.System;
        _heartbeatTimeout = TimeSpan.FromSeconds(heartbeatTimeoutSeconds);
        _state = store.Load();
        Require(_state.Initialized && _state.Accounts.Any(a => a.Role == AccountRole.Administrator),
            "setup_required", "로컬 최초 설정을 완료하세요.", 503);
        RecoverStartup(); RecoverCameras();
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private void Healthy() => Require(!_storageFailed && !_stopping, "host_unavailable", "호스트 저장 또는 종료 상태를 확인하세요.", 503);
    private void Persist(HostState next)
    {
        next.Revision++;
        try { _store.Save(next); _state = next; }
        catch { _storageFailed = true; throw; } // No in-memory success or alternate database on failed commit.
    }
    private T Change<T>(Func<HostState, T> action)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var next = JsonDefaults.Copy(_state);
            var result = action(next);
            Persist(next);
            return JsonDefaults.Copy(result);
        }
    }
    private void Audit(HostState s, Guid? user, string action, string detail) =>
        s.Audit.Add(AuditPresentation.Enrich(new(Now, user, action, detail), s));
    private Session Authenticate(string token)
    {
        Require(!string.IsNullOrWhiteSpace(token) && _sessions.TryGetValue(Digest(token), out _),
            "unauthorized", "로그인이 필요합니다.", 401);
        var session = _sessions[Digest(token)];
        Require(Now - session.CreatedAt < TimeSpan.FromHours(12), "session_expired", "다시 로그인하세요.", 401);
        var user = _state.Accounts.Single(a => a.Id == session.Info.UserId);
        Require(user.Enabled, "account_disabled", "계정이 비활성화되었습니다.", 403);
        return session;
    }
    private Account User(HostState s, Session session) => s.Accounts.Single(a => a.Id == session.Info.UserId);
    private Session Owner(HostState s, string token, long generation)
    {
        var session = Authenticate(token);
        Require(!s.FencedSessions.Contains(session.Info.Id), "session_fenced", "이전 세션이 차단되었습니다. 다시 로그인하세요.", 403);
        Require(s.Lease.Mode == LeaseMode.Held && s.Lease.SessionId == session.Info.Id &&
            s.Lease.Generation == generation, "lease_required", "현재 사용권과 세대가 필요합니다.", 403);
        Require(User(s, session).Role != AccountRole.Viewer, "forbidden", "조회 계정은 제어할 수 없습니다.", 403);
        return session;
    }
    private Session Admin(HostState s, string token)
    {
        var session = Authenticate(token);
        Require(User(s, session).Role == AccountRole.Administrator, "admin_required", "관리자 권한이 필요합니다.", 403);
        return session;
    }
    private static bool CanControl(Account account, Guid deviceId) =>
        account.Enabled && account.Role != AccountRole.Viewer && (account.AllDevices || account.DeviceIds.Contains(deviceId));
    private static bool HoldsReservations(Job j) => j.Active;
    private void Fence(HostState s, string reason)
    {
        s.Lease.Mode = LeaseMode.RecoveryRequired;
        s.Lease.Generation++;
        s.Lease.LostAt = Now;
        s.Lease.FencedAt = Now;
        s.Lease.RecoveryReason = reason;
        if (s.Lease.SessionId is Guid id && !s.FencedSessions.Contains(id)) s.FencedSessions.Add(id);
        Audit(s, null, "ConnectionLost", reason);
        Audit(s, null, "PreviousSessionFenced", $"generation={s.Lease.Generation}");
    }
    private void CheckConnectionUnsafe()
    {
        if (_state.Lease.Mode != LeaseMode.Held) return;
        var session = _sessions.Values.FirstOrDefault(s => s.Info.Id == _state.Lease.SessionId);
        if (session is not null && Now - session.LastSeen < _heartbeatTimeout) return;
        var next = JsonDefaults.Copy(_state);
        Fence(next, "사용 세션 생존 확인 시간 초과");
        Persist(next);
    }
    public void CheckConnections() { lock (_gate) { Healthy(); CheckConnectionUnsafe(); } }
    public LoginResult Login(LoginRequest request)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            AccountName(request.UserName); Text(request.PcName, "앱 PC 이름");
            Require(request.PcId != Guid.Empty && request.Password is { Length: <= 256 },
                "invalid_input", "앱 PC ID와 비밀번호를 확인하세요.", 400);
            var account = _state.Accounts.SingleOrDefault(a => a.Name.Equals(request.UserName.Trim(), StringComparison.OrdinalIgnoreCase));
            Require(account is not null && account.Enabled && _passwords.Verify(request.Password, account.PasswordHash),
                "login_failed", "계정 또는 비밀번호를 확인하세요.", 401);
            foreach (var key in _sessions.Where(p => Now - p.Value.CreatedAt >= TimeSpan.FromHours(12)).Select(p => p.Key).ToArray())
                _sessions.Remove(key);
            Require(_sessions.Count < 1000, "session_limit", "호스트 세션 한도를 초과했습니다.", 429);
            var info = new SessionInfo(Guid.NewGuid(), account!.Id, account.Name, account.Role, request.PcId, request.PcName);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var next = JsonDefaults.Copy(_state);
            Audit(next, account.Id, "Login", $"session={info.Id}; pc={info.PcName}");
            Persist(next);
            _sessions.Add(Digest(token), new(info, Now));
            return new(token, info);
        }
    }
    public StateView GetState(string token)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var session = Authenticate(token);
            var s = _state;
            var lease = JsonDefaults.Copy(s.Lease);
            if (lease.Mode == LeaseMode.Held)
                lease.LastSeenAt = _sessions.Values.FirstOrDefault(x => x.Info.Id == lease.SessionId)?.LastSeen;
            return JsonDefaults.Copy(new StateView(s.SiteId, s.SiteName, s.Revision, lease, session.Info,
                s.Devices.ToArray(), s.DeviceStates, s.Roles.ToArray(), s.Scenarios.ToArray(), s.Jobs.ToArray(),
                s.UncertainDevices.ToArray(), User(s, session).Role == AccountRole.Administrator
                    ? s.Accounts.Select(a => new AccountView(a.Id, a.Name, a.Role, a.Enabled, a.AllDevices, a.DeviceIds.ToArray())).ToArray() : [],
                s.Audit.TakeLast(200).Select(a => AuditPresentation.Enrich(a, s)).ToArray(), _driver.Models, (int)_heartbeatTimeout.TotalSeconds)
                { HiperwallWriteSupported = _hiperwall is IHiperwallWriter, CanControlHiperwall = HiperwallPermission(User(s, session)), HiperwallReadSupported = _hiperwall is not null, HiperwallConfigurationVersion = s.Hiperwall?.Version ?? 0,
                    OutstandingHiperwallEdits = s.HiperwallEdits.Where(r => r.NeedsAttention).OrderByDescending(r => r.AcceptedAt).ToArray(),
                    OutstandingHiperwallDisplays = s.HiperwallDisplays.Where(j => j.Outstanding).ToArray(),
                    HiperwallDisplayJobs = s.HiperwallDisplays.Where(j => j.Outstanding).Union(s.HiperwallDisplays.TakeLast(100)).Reverse().ToArray(),
                    HiperwallLayoutsSupported = _hiperwall is IHiperwallWriter,
                    CameraSupported = _media is not null, MediaConfigurationVersion = s.Media?.Version ?? 0,
                    LightCardsSupported = true, LightGroupsSupported = true, LightBatchSupported = true, LightLayout = CurrentLightLayout(s),
                    ControllableDeviceIds = s.Devices.Where(d => CanControl(User(s, session), d.Id)).Select(d => d.Id).ToArray() });
        }
    }
    public Lease Acquire(string token) => Change(s =>
    {
        var session = Authenticate(token);
        Require(!s.FencedSessions.Contains(session.Info.Id), "session_fenced", "차단된 세션입니다. 다시 로그인하세요.", 403);
        Require(User(s, session).Role != AccountRole.Viewer, "forbidden", "조회 계정은 사용권을 획득할 수 없습니다.", 403);
        if (s.Lease.Mode == LeaseMode.Held && s.Lease.SessionId == session.Info.Id) return s.Lease;
        Require(s.Lease.Mode == LeaseMode.Free, "lease_busy", "다른 세션 사용 중 또는 관리자 복구가 필요합니다.");
        session.LastSeen = Now;
        s.Lease = new Lease { Mode = LeaseMode.Held, Generation = s.Lease.Generation + 1,
            SessionId = session.Info.Id, UserId = session.Info.UserId, UserName = session.Info.UserName,
            PcName = session.Info.PcName, LastSeenAt = Now };
        Audit(s, session.Info.UserId, "Acquire", $"session={session.Info.Id}; generation={s.Lease.Generation}");
        return s.Lease;
    });
    public Lease Heartbeat(string token, long generation)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe(); // An overdue heartbeat cannot revive a fenced session.
            var session = Owner(_state, token, generation);
            session.LastSeen = Now;
            var lease = JsonDefaults.Copy(_state.Lease); lease.LastSeenAt = Now; return lease;
        }
    }
    public Lease Release(string token, long generation) => Change(s =>
    {
        var session = Owner(s, token, generation);
        s.Lease = new Lease { Mode = LeaseMode.Free, Generation = s.Lease.Generation + 1 };
        Audit(s, session.Info.UserId, "ReleasePreservingJobs", $"session={session.Info.Id}");
        return s.Lease;
    });
    public bool Logout(string token)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var session = Authenticate(token);
            var next = JsonDefaults.Copy(_state);
            if (next.Lease.Mode == LeaseMode.Held && next.Lease.SessionId == session.Info.Id)
                next.Lease = new Lease { Mode = LeaseMode.Free, Generation = next.Lease.Generation + 1 };
            if (!next.FencedSessions.Contains(session.Info.Id)) next.FencedSessions.Add(session.Info.Id);
            Audit(next, session.Info.UserId, "LogoutPreservingJobs", $"session={session.Info.Id}");
            Persist(next);
            // Commit and revoke under the same lock. Accepted jobs retain their requester snapshots.
            _sessions.Remove(Digest(token));
            CancelHiperwallSession(session.Info.Id);
            return true;
        }
    }
    public RecoveryReview ReviewRecovery(string token)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe();
            var session = Admin(_state, token);
            Require(_state.Lease.Mode == LeaseMode.RecoveryRequired && _state.Lease.FencedAt is not null,
                "recovery_order", "이전 세션 차단 확인 후 진행 작업을 검토할 수 있습니다.");
            var id = Guid.NewGuid();
            _reviews.Clear();
            _reviews[id] = new(session.Info.UserId, _state.Lease.Generation, Now, RecoveryFingerprint(_state));
            var next = JsonDefaults.Copy(_state);
            Audit(next, session.Info.UserId, "RecoveryReviewed", $"review={id}");
            Persist(next);
            return new RecoveryReview(id, _state.Lease.Generation, Now, JsonDefaults.Copy(_state.Jobs.ToArray()), _state.UncertainDevices.ToArray())
                { HiperwallEdits = JsonDefaults.Copy(_state.HiperwallEdits.Where(r => r.Active || r.Steps.Any(s => s.State == HiperwallSendState.Unknown)).ToArray()),
                  HiperwallDisplays = JsonDefaults.Copy(_state.HiperwallDisplays.Where(j => j.Outstanding).ToArray()) };
        }
    }
    private static string RecoveryFingerprint(HostState state) => Digest(System.Text.Json.JsonSerializer.Serialize(
        new { state.Jobs, state.UncertainDevices, HiperwallDisplays = state.HiperwallDisplays.Where(j => j.Outstanding).Select(j => new {
            j.Request, j.Requester, j.CloseAt, j.StopRequested, j.StoppedBy,
            Targets = j.Targets.Select(t => new { t.Command, t.OpenState, t.CleanupState, t.OpenAttempted, t.CleanupAttempts, t.Message })
        }).ToArray(), HiperwallEdits = state.HiperwallEdits.Where(r => r.Active || r.Steps.Any(s => s.State == HiperwallSendState.Unknown)).ToArray() }, JsonDefaults.Options));
    public Lease ApproveRecovery(string token, Guid reviewId) => Change(s =>
    {
        var session = Admin(s, token);
        Require(_reviews.TryGetValue(reviewId, out _), "review_required", "진행 작업을 먼저 확인하세요.");
        var review = _reviews[reviewId];
        Require(s.Lease.Mode == LeaseMode.RecoveryRequired && s.Lease.FencedAt is not null &&
            review.UserId == session.Info.UserId && review.Generation == s.Lease.Generation &&
            Now - review.At < TimeSpan.FromMinutes(5) && review.Fingerprint == RecoveryFingerprint(s),
            "review_stale", "작업 상태가 바뀌었습니다. 진행 작업을 다시 확인하세요.");
        s.Lease = new Lease { Mode = LeaseMode.Free, Generation = s.Lease.Generation + 1 };
        Audit(s, session.Info.UserId, "RecoveryApproved", $"review={reviewId}; 작업 및 불확실 장비 보존");
        return s.Lease;
    });
    public void StopAccepting()
    {
        lock (_gate) { _stopping = true; MarkDisplaysForShutdown(); _mediaStopping.Cancel(); foreach (var query in _hiperwallQueries.Values) query.Cancel(); }
    }
}
