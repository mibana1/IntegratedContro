using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel : Bindable
{
    public HiperwallViewModel Hiperwall { get; } = new();
    public CameraViewModel Cameras { get; }
    public LightingViewModel Lighting { get; }
    public ScenarioEditorViewModel ScenarioEditor { get; }
    public DeviceSettingsViewModel DeviceSettings { get; }
    public DeviceControlViewModel DeviceControl { get; }
    public AccountManagementViewModel AccountManagement { get; }
    public JobManagementViewModel JobManagement { get; }
    public RecoveryViewModel Recovery { get; }
    private int _deviceViewIndex;
    public int DeviceViewIndex { get => _deviceViewIndex; set => Set(ref _deviceViewIndex, value); }
    private ClientPreferences _preferences;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<AsyncCommand> _commands = [];
    private HostClient? _client;
    private StateView? _state;
    private LoginResult? _login;
    private SubmitRequest? _pending;
    private LightBatchRequest? _pendingLightBatch;
    private RestoreLightSlotRequest? _pendingLightSlot;
    private Guid? PendingRequestId => _pending?.RequestId ?? _pendingLightBatch?.RequestId ?? _pendingLightSlot?.RequestId;
    private bool HasPending => PendingRequestId is not null;
    private bool _busy, _polling, _closing, _connected;
    private readonly CancellationTokenSource _lifetime = new();
    private string _message = "접속 설정을 확인하고 앱 계정으로 로그인하세요.";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool IsBusy => _busy;
    public bool CanEditLogin => !_busy && !_closing;
    public bool IsLoggedIn => _login is not null;
    public bool CanControl => _connected && _state?.Lease.Mode == LeaseMode.Held &&
        _state.Lease.SessionId == _login?.Session.Id;
    public bool IsAdmin => _connected && _login?.Session.Role == AccountRole.Administrator;
    public bool CanConfigure => CanControl && IsAdmin;
    public string ConnectionSummary => _connected ? $"HTTPS 연결 · 마지막 확인 {DateTime.Now:HH:mm:ss}" : "미연결 / 표시된 이전 상태를 최신 관측으로 사용하지 마세요.";
    public string PendingSummary => !HasPending ? "" : $"접수 결과 확인 필요: {PendingRequestId} · 같은 요청 ID로만 재확인합니다.";
    public string Endpoint { get; set; }
    public string Fingerprint { get; set; }
    public string LoginName { get; set; } = "";
    public Func<string> ReadLoginPassword { get; set; } = () => "";
    public Action ClearLoginPassword { get; set; } = () => { };
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand AcquireCommand { get; }
    public AsyncCommand ReleaseCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand RetryCommand { get; }
    private long Generation => _state?.Lease.Generation ?? 0;
    private HostClient Client => _client ?? throw new InvalidOperationException("먼저 로그인하세요.");

    public MainViewModel()
    {
        var featureHost = new FeatureHost(this);
        Lighting = new LightingViewModel(featureHost);
        ScenarioEditor = new ScenarioEditorViewModel(featureHost);
        var initialPreferences = ClientPreferences.ReadForStartup();
        _preferences = initialPreferences.Preferences;
        DeviceSettings = new DeviceSettingsViewModel(featureHost, _preferences.PcId);
        DeviceControl = new DeviceControlViewModel(featureHost);
        AccountManagement = new AccountManagementViewModel(featureHost);
        JobManagement = new JobManagementViewModel(featureHost);
        Recovery = new RecoveryViewModel(featureHost);
        Lighting.DeviceDetailsRequested += id => { DeviceSettings.SelectedDevice = DeviceSettings.Devices.SingleOrDefault(d => d.Id == id); DeviceViewIndex = 1; };
        DeviceSettings.RoleAssigned += id => DeviceControl.SelectedRole = DeviceControl.Roles.SingleOrDefault(r => r.Id == id);
        Cameras = new CameraViewModel(Hiperwall, AppAdapters.CreateVideoAsync);
        Cameras.StatusReported += message => Message = message;
        Endpoint = _preferences.Endpoint; Fingerprint = _preferences.Fingerprint;
        LoginName = _preferences.LastLoginName ?? "";
        InitializeLoginSettings(initialPreferences);
        LoginCommand = Command(Login, () => !IsLoggedIn && IsLoginPage && !HasPreferencesRecovery);
        LogoutCommand = Command(Logout, () => IsLoggedIn);
        AcquireCommand = Command(async () => { await Client.Post<Lease>("/api/lease/acquire"); }, () => _connected && IsLoggedIn && _state?.Lease.Mode == LeaseMode.Free);
        ReleaseCommand = Command(async () => { await Client.Post<Lease>("/api/lease/release", new LeaseRequest(Generation)); Message = "사용 종료 완료. 접수 작업과 예약은 호스트에서 유지됩니다."; }, () => CanControl);
        RefreshCommand = Command(Refresh, () => IsLoggedIn);
        RetryCommand = Command(SendPending, () => _connected && HasPending);
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? available = null)
    {
        var command = new AsyncCommand(() => RunCommand(action), () => !_busy && !_closing && (available?.Invoke() ?? true));
        _commands.Add(command); return command;
    }
    private async Task RunCommand(Func<Task> action)
    {
        _busy = true; Notify();
        try { await action(); if (IsLoggedIn && !_closing) await Refresh(); }
        catch (Exception error) { if (!_closing) Report(error); }
        finally { _busy = false; Notify(); }
    }
    private void Report(Exception error)
    {
        if (error is ApiException { Code: "login_failed" } loginError) Message = loginError.Message;
        else if (error is ApiException api && (api.Status == HttpStatusCode.Unauthorized || api.Code == "account_disabled"))
        { _connected = false; _login = null; _state = null; Message = "인증 세션이 만료되었거나 계정이 차단되었습니다. 다시 로그인하세요."; }
        else if (error is HttpRequestException or TaskCanceledException || error is ApiException { Status: HttpStatusCode.ServiceUnavailable }) { _connected = false; Message = "연결 이상: 신규 제어 차단. 호스트 연결과 관리자 복구 상태를 확인하세요."; }
        else Message = error.Message;
        Notify();
    }
    private async Task Login()
    {
        var password = ReadLoginPassword();
        try
        {
            _client?.Dispose(); _client = new(Endpoint, Fingerprint);
            var login = await Client.Post<LoginResult>("/api/login",
                new LoginRequest(LoginName, password, _preferences.PcId, Environment.MachineName), _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _login = login;
        }
        finally { ClearLoginPassword(); }
        Client.SetToken(_login.Token); _state = null; _connected = true;
        LoginName = _login.Session.UserName; Changed(nameof(LoginName));
        var preferences = _preferences with { Endpoint = Endpoint, Fingerprint = Fingerprint, LastLoginName = LoginName };
        try
        {
            preferences.Save(); _preferences = preferences;
            Message = "로그인 완료. 조회 모드에서 현황을 확인하고 사용 시작을 선택하세요.";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        { Message = "로그인은 완료했지만 최근 아이디를 저장하지 못했습니다. 앱 설정 폴더를 확인하세요."; }
    }
    private async Task Logout()
    {
        Hiperwall.Close(); Cameras.UpdateContext(null, null, false, false, 0); await Cameras.StopPlaybackAsync();
        try { await Client.Post<bool>("/api/logout"); Message = "로그아웃 완료. 접수 작업은 호스트에서 계속 처리합니다."; }
        finally
        {
            _login = null; _connected = false; _state = null;
            _pending = null; _pendingLightBatch = null; _pendingLightSlot = null;
        }
    }
    private async Task Poll()
    {
        if (_polling || _closing || !IsLoggedIn) return;
        _polling = true;
        try
        {
            if (_state?.Lease.Mode == LeaseMode.Held && _state.Lease.SessionId == _login?.Session.Id)
            {
                try { await Client.Post<Lease>("/api/lease/heartbeat", new LeaseRequest(Generation)); }
                catch (ApiException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict) { }
            }
            if (!_busy) await Refresh();
        }
        catch (Exception error) { Report(error); }
        finally { _polling = false; }
    }
    private async Task Refresh()
    {
        var sessionId = _login?.Session.Id;
        var state = await Client.Get<StateView>("/api/state");
        if (_closing || _login?.Session.Id != sessionId || _login is null || (_state is not null && state.Revision < _state.Revision)) return;
        _state = state; _connected = true;
        if (HasPending && state.Jobs.Any(j => j.Snapshot.RequestId == PendingRequestId))
        { _pending = null; _pendingLightBatch = null; _pendingLightSlot = null; Message = "요청 ID로 호스트 접수 기록을 확인했습니다."; }
        Notify();
    }
    private async Task SendPending()
    {
        if (!HasPending) return;
        try
        {
            var job = _pendingLightSlot is not null ? await Client.Post<Job>("/api/lights/slots/restore", _pendingLightSlot)
                : _pendingLightBatch is not null ? await Client.Post<Job>("/api/lights/power", _pendingLightBatch) : await Client.Post<Job>("/api/jobs", _pending);
            _pending = null; _pendingLightBatch = null; _pendingLightSlot = null; Message = $"접수 완료: {job.Id}. 실행 결과는 작업 탭에서 확인하세요.";
        }
        catch (ApiException error) when ((int)error.Status < 500) { _pending = null; _pendingLightBatch = null; _pendingLightSlot = null; throw; }
        finally { Notify(); }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanEditLogin), nameof(IsLoggedIn), nameof(IsAdmin), nameof(CanControl), nameof(CanConfigure),
            nameof(ConnectionSummary), nameof(PendingSummary) }) Changed(name);
        NotifyMyInfo();
        var featureContext = new FeatureContext(_state, _connected, CanControl, CanConfigure, _busy, _closing, HasPending);
        Lighting.UpdateContext(featureContext);
        ScenarioEditor.UpdateContext(featureContext);
        DeviceSettings.UpdateContext(featureContext);
        DeviceControl.UpdateContext(featureContext);
        AccountManagement.UpdateContext(featureContext);
        JobManagement.UpdateContext(featureContext);
        Recovery.UpdateContext(featureContext);
        Hiperwall.UpdateDisplayJobs(_state?.HiperwallDisplayJobs ?? [], _state?.HiperwallLayoutsSupported == true);
        Cameras.Generation = Generation;
        Cameras.UpdateContext(IsLoggedIn && !_closing ? _client : null, !_closing ? _login?.Session.Id : null,
            CanConfigure, _state?.CameraSupported ?? false, _state?.MediaConfigurationVersion ?? 0, connected: _connected);
        Hiperwall.Generation = Generation;
        Hiperwall.UpdateContext(IsLoggedIn && !_closing ? _client : null, !_closing ? _login?.Session.Id : null,
            IsAdmin, CanConfigure, _state?.HiperwallReadSupported ?? false, _state?.HiperwallConfigurationVersion ?? 0, CanControl && (_state?.CanControlHiperwall ?? false), _state?.HiperwallWriteSupported ?? false, connected: _connected);
        Hiperwall.UpdateSlots(_state?.HiperwallSlots ?? [], _state?.HiperwallSlotsSupported == true);
        foreach (var command in _commands) command.Raise();
    }
    public async Task CloseAsync()
    {
        _closing = true; _lifetime.Cancel(); Hiperwall.Close(); _timer.Stop(); Notify(); await Cameras.CloseAsync();
        try { if (IsLoggedIn) await Client.Post<bool>("/api/logout"); }
        catch (Exception) { /* The host fences on missed heartbeat; accepted work remains authoritative. */ }
        finally { _client?.Dispose(); }
    }
}
