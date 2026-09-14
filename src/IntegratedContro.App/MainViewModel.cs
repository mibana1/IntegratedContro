using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class DeviceRow(DeviceConfig config, string state, string desired, string connection, string result, string restriction) : Bindable
{
    public DeviceConfig Config { get; private set; } = config;
    public string State { get; private set; } = state;
    public string Desired { get; private set; } = desired;
    public string Connection { get; private set; } = connection;
    public string Result { get; private set; } = result;
    public string Restriction { get; private set; } = restriction;
    public string Label => $"{Name} / {PcName} · {Id.ToString()[..8]}";
    public void Update(DeviceRow current)
    {
        Config = current.Config; State = current.State; Desired = current.Desired;
        Connection = current.Connection; Result = current.Result; Restriction = current.Restriction;
        foreach (var name in new[] { nameof(Config), nameof(Name), nameof(PcName), nameof(Model), nameof(Label),
            nameof(State), nameof(Desired), nameof(Connection), nameof(Result), nameof(Restriction) }) Changed(name);
    }
    public Guid Id => Config.Id;
    public string Name => Config.Name;
    public string PcName => Config.PcName;
    public string Model => Config.ModelId;
}
public sealed record JobRow(Job Job, bool PreviousSession)
{
    public Guid Id => Job.Id;
    public string Name => Job.Snapshot.Name;
    public string Requester => $"{Job.Snapshot.RequesterName} / {Job.Snapshot.ClientPcName}";
    public string OriginSession => Job.Snapshot.SessionId.ToString()[..8];
    public string Kind => Job.IsLightBatch ? "일괄 조명" : Job.Kind == JobKind.Scenario ? "시나리오" : "일반 명령";
    public string Targets => string.Join(", ", Job.Snapshot.Steps.Select(x => $"{x.Target.Name} ({x.Target.PcName})").Distinct());
    public string Progress => $"{Job.Steps.Count(x => x.Status != StepStatus.Pending && x.Status != StepStatus.Dispatching)}/{Job.Steps.Count}";
    public string Status => Job.Status switch
    {
        JobStatus.Queued => "접수·대기", JobStatus.Running => "실행 중", JobStatus.StopRequested => "취소 요청·결과 대기",
        JobStatus.Completed => Job.Steps.Any(s => s.Status == StepStatus.Succeeded) ? "명령 처리 종료" : "가상 실행 종료", JobStatus.Cancelled => "미전송 부분 취소", JobStatus.Interrupted => "중단",
        _ => "불확실·대조 필요"
    };
    public string AcceptedAt => Job.Snapshot.AcceptedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Result => Job.Result;
}
public sealed partial class MainViewModel : Bindable
{
    public HiperwallViewModel Hiperwall { get; } = new();
    private readonly ClientPreferences _preferences = ClientPreferences.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<AsyncCommand> _commands = [];
    private HostClient? _client;
    private StateView? _state;
    private LoginResult? _login;
    private SubmitRequest? _pending;
    private LightBatchRequest? _pendingLightBatch;
    private bool HasPending => _pending is not null || _pendingLightBatch is not null;
    private RecoveryReview? _review;
    private bool _busy, _polling, _closing, _connected;
    private string _message = "호스트의 최초 설정을 완료한 뒤 주소·인증서 지문·앱 계정으로 접속하세요.";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool IsBusy => _busy;
    public bool CanEditLogin => !_busy;
    public bool IsLoggedIn => _login is not null;
    public bool CanControl => _connected && _state?.Lease.Mode == LeaseMode.Held &&
        _state.Lease.SessionId == _login?.Session.Id;
    public bool IsAdmin => _connected && _login?.Session.Role == AccountRole.Administrator;
    public bool CanConfigure => CanControl && IsAdmin;
    public string SiteTitle => _state?.SiteName ?? "IntegratedContro";
    public string UserSummary => _login is null ? "로그인 전" : $"{_login.Session.UserName} · {Environment.MachineName} · 세션 {_login.Session.Id.ToString()[..8]}";
    public string LeaseSummary => !_connected && IsLoggedIn ? "연결 이상 · 신규 제어 차단" : _state?.Lease.Mode switch
    {
        LeaseMode.Held => $"사용 중: {_state.Lease.UserName} / {_state.Lease.PcName} · 세대 {_state.Lease.Generation}",
        LeaseMode.RecoveryRequired => "복구 필요 · 이전 세션 신규 요청 차단됨",
        LeaseMode.Free => "조회 모드 · 사용 시작 가능", _ => "호스트에 연결되지 않음"
    };
    public string PreviousSummary => _state is null ? "이전 사용자 작업 0건" :
        $"이전 사용자 작업 {_state.Jobs.Count(j => j.Snapshot.SessionId != _login?.Session.Id && (j.Active || j.Status == JobStatus.NeedsReview)) +
            _state.OutstandingHiperwallEdits.Count(r => r.Requester.Id != _login?.Session.Id && r.NeedsAttention)}건 · Hiperwall 포함 · 사용 종료 후에도 호스트에서 유지";
    public string ConnectionSummary => _connected ? $"HTTPS 연결 · 마지막 확인 {DateTime.Now:HH:mm:ss}" : "미연결 / 표시된 이전 상태를 최신 관측으로 사용하지 마세요.";
    public string PendingSummary => !HasPending ? "" : $"접수 결과 확인 필요: {_pending?.RequestId ?? _pendingLightBatch?.RequestId} · 같은 요청 ID로만 재확인합니다.";
    public string RecoverySummary => _state?.Lease.Mode != LeaseMode.RecoveryRequired ? "복구 인계가 필요한 사용권이 없습니다." :
        $"1. 연결 이상: {_state.Lease.LostAt:O}\n2. 이전 세션 차단 확인: {_state.Lease.FencedAt:O}\n사유: {_state.Lease.RecoveryReason}\n3. 진행 작업 확인 후 4. 관리자 복구 인계를 승인하세요.";
    public string ReviewText { get; private set; } = "진행 작업 확인 버튼을 누르면 그 시점의 작업·예약·불확실 대상을 표시합니다.";
    public string Endpoint { get; set; }
    public string Fingerprint { get; set; }
    public string LoginName { get; set; } = "";
    public Func<string> ReadLoginPassword { get; set; } = () => "";
    public Action ClearLoginPassword { get; set; } = () => { };
    public Func<string> ReadNewPassword { get; set; } = () => "";
    public Action ClearNewPassword { get; set; } = () => { };
    public Func<string, bool> ConfirmManualSwitch { get; set; } = _ => false;
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<RoleBinding> Roles { get; } = [];
    public ObservableCollection<JobRow> Jobs { get; } = [];
    public ObservableCollection<ScenarioDefinition> Scenarios { get; } = [];
    public ObservableCollection<DeviceModel> Models { get; } = [];
    public ObservableCollection<AccountView> Accounts { get; } = [];
    public ObservableCollection<ScenarioStep> DraftSteps { get; } = [];
    public ObservableCollection<AuditRow> Audit { get; } = [];
    private AuditRow? _selectedAudit;
    public AuditRow? SelectedAudit { get => _selectedAudit; set { Set(ref _selectedAudit, value); Changed(nameof(AuditDetails)); } }
    public string AuditDetails => SelectedAudit?.Raw ?? "기록을 선택하면 원본 이벤트 코드와 ID를 확인할 수 있습니다.";
    public IEnumerable<AccountRole> AccountRoles => Enum.GetValues<AccountRole>();
    public IEnumerable<VirtualFault> Faults => Enum.GetValues<VirtualFault>();
    public IEnumerable<FailurePolicy> FailurePolicies => Enum.GetValues<FailurePolicy>();
    private DeviceRow? _selectedDevice;
    public DeviceRow? SelectedDevice { get => _selectedDevice; set { Set(ref _selectedDevice, value); Notify(); } }
    private RoleBinding? _selectedRole;
    public RoleBinding? SelectedRole
    {
        get => _selectedRole;
        set { Set(ref _selectedRole, value); Changed(nameof(Capabilities)); SelectedCapability = Capabilities.FirstOrDefault(); }
    }
    public IEnumerable<Capability> Capabilities => _state?.Models.FirstOrDefault(m =>
        m.Id == _state.Devices.FirstOrDefault(d => d.Id == SelectedRole?.DeviceId)?.ModelId)?.Capabilities ?? [];
    private Capability? _selectedCapability;
    public Capability? SelectedCapability
    {
        get => _selectedCapability;
        set { Set(ref _selectedCapability, value); if (value is not null) { CommandValue = value.Minimum; Changed(nameof(CommandValue)); } Changed(nameof(CommandRange)); }
    }
    public string CommandRange => SelectedCapability is null ? "역할을 선택하세요." : $"{SelectedCapability.Minimum}~{SelectedCapability.Maximum} {SelectedCapability.Unit}";
    private int _commandValue = 1; public int CommandValue { get => _commandValue; set => Set(ref _commandValue, value); }
    private int _delayMs; public int DelayMs { get => _delayMs; set => Set(ref _delayMs, value); }
    private int _timeoutMs = 3000; public int TimeoutMs { get => _timeoutMs; set => Set(ref _timeoutMs, value); }
    public FailurePolicy DraftFailurePolicy { get; set; }
    public string ConditionOperationText { get; set; } = "";
    public string ConditionValueText { get; set; } = "";
    public ScenarioStep? SelectedDraftStep { get; set; }
    private JobRow? _selectedJob;
    public JobRow? SelectedJob { get => _selectedJob; set { Set(ref _selectedJob, value); Changed(nameof(JobDetails)); Notify(); } }
    public string JobDetails
    {
        get
        {
            if (SelectedJob is null) return "작업을 선택하면 요청자·취소자·고정 대상·단계별 결과를 확인할 수 있습니다.";
            var job = SelectedJob.Job; var snapshot = job.Snapshot;
            return $"작업: {snapshot.Name} | {SelectedJob.Status}\n원 요청자: {snapshot.RequesterName} / {snapshot.ClientPcName} / 접수 {snapshot.AcceptedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                $"원 세션: {snapshot.SessionId} | 사용권 세대: {snapshot.LeaseGeneration}\n작업 ID: {job.Id} | 요청 ID: {snapshot.RequestId}\n" +
                $"취소 요청자: {job.CancellerName ?? "없음"} | 취소 시각: {job.CancelRequestedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                $"실행 결과: {job.Result}\n만료: {snapshot.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | 가상 모드 | 시나리오 버전: {snapshot.ScenarioVersion}\n\n" +
                string.Join("\n\n", snapshot.Steps.Select((step, i) =>
                    $"{i + 1}. {step.Role.Id} → {step.Target.Name} / PC: {step.Target.PcName}\n" +
                    $"   동작: {step.Operation} = {step.Value} {step.Unit} | 대기 {step.DelayBeforeMs}ms / 제한 {step.TimeoutMs}ms / 실패 정책 {step.OnFailure}\n" +
                    $"   고정 PC ID: {step.Target.PcId} / 장비 ID: {step.Target.Id}\n" +
                    $"   모델: {step.Target.ModelId} / 연결: {step.Target.ConnectionId} / 장비 설정 v{step.Target.Version} / 역할 v{step.Role.Version}\n" +
                    $"   확인 조건: {step.ConditionOperation} = {step.ConditionValue} | 단계: {job.Steps[i].Status} / {job.Steps[i].Result}\n" +
                    $"   확인 근거: {DeviceEvidence.Label(job.Steps[i].Evidence?.Confirmation ?? ConfirmationLevel.None)} | 결과: {job.Steps[i].Evidence?.Outcome}\n" +
                    $"   {DeviceEvidence.RecordedObservations(job.Steps[i].Evidence)}"));
        }
    }
    public ScenarioDefinition? SelectedScenario { get; set; }
    public DeviceModel? SelectedModel { get; set; }
    public string DeviceIdText { get; set; } = Guid.NewGuid().ToString();
    public string PcIdText { get; set; }
    public string PcName { get; set; } = Environment.MachineName;
    public string DeviceName { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public bool DeviceEnabled { get; set; } = true;
    public VirtualFault DeviceFault { get; set; }
    public int DeviceLatencyMs { get; set; } = 50;
    public int DeviceExpectedVersion { get; private set; }
    private string _roleName = "";
    public string RoleName { get => _roleName; set { if (Set(ref _roleName, value)) Notify(); } }
    public string RoleTargetSummary => SelectedDevice is null ? "배정 대상: 장비를 선택하세요." :
        $"배정 대상: {SelectedDevice.Name} / {SelectedDevice.PcName}\n장비 ID: {SelectedDevice.Id}";
    public string RoleAssignmentHint => !IsLoggedIn ? "호스트에 접속한 뒤 관리자 계정으로 사용 시작을 누르세요." :
        !IsAdmin ? "역할 배정은 관리자만 할 수 있습니다." : !CanControl ? "역할 배정에는 사용권이 필요합니다. 사용 시작을 누르세요." :
        SelectedDevice is null ? "목록에서 배정할 장비를 먼저 선택하세요." : string.IsNullOrWhiteSpace(RoleName) ?
        "역할 ID를 입력하세요. 예: room.light" : "배정 후 장비 제어의 역할로 조작에서 기능과 값을 선택하세요. 동일 역할 ID는 선택 장비로 재배정됩니다.";
    public string ScenarioName { get; set; } = "";
    private Guid _scenarioId = Guid.NewGuid();
    private int _scenarioVersion;
    public string NewAccountName { get; set; } = "";
    public AccountRole NewAccountRole { get; set; } = AccountRole.Operator;
    public bool AccountAllDevices { get; set; } = true;
    public string AccountDeviceIds { get; set; } = "";
    public AccountView? SelectedAccount { get; set; }
    public bool AccountEnabled { get; set; } = true;
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand AcquireCommand { get; }
    public AsyncCommand ReleaseCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SubmitCommand { get; }
    public AsyncCommand RetryCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand ManualSwitchCommand { get; }
    public AsyncCommand ReconcileCommand { get; }
    public AsyncCommand SaveDeviceCommand { get; }
    public AsyncCommand LoadDeviceCommand { get; }
    public AsyncCommand NewDeviceCommand { get; }
    public AsyncCommand SaveRoleCommand { get; }
    public AsyncCommand AddStepCommand { get; }
    public AsyncCommand RemoveStepCommand { get; }
    public AsyncCommand SaveScenarioCommand { get; }
    public AsyncCommand LoadScenarioCommand { get; }
    public AsyncCommand NewScenarioCommand { get; }
    public AsyncCommand RunScenarioCommand { get; }
    public AsyncCommand CreateAccountCommand { get; }
    public AsyncCommand UpdateAccountCommand { get; }
    public AsyncCommand LoadAccountCommand { get; }
    public AsyncCommand ReviewCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    private long Generation => _state?.Lease.Generation ?? 0;
    private HostClient Client => _client ?? throw new InvalidOperationException("먼저 로그인하세요.");

    public MainViewModel()
    {
        Endpoint = _preferences.Endpoint; Fingerprint = _preferences.Fingerprint; PcIdText = _preferences.PcId.ToString();
        LoginCommand = Command(Login, () => !IsLoggedIn);
        LogoutCommand = Command(Logout, () => IsLoggedIn);
        AcquireCommand = Command(async () => { await Client.Post<Lease>("/api/lease/acquire"); }, () => _connected && IsLoggedIn && _state?.Lease.Mode == LeaseMode.Free);
        ReleaseCommand = Command(async () => { await Client.Post<Lease>("/api/lease/release", new LeaseRequest(Generation)); Message = "사용 종료 완료. 접수 작업과 예약은 호스트에서 유지됩니다."; }, () => CanControl);
        RefreshCommand = Command(Refresh, () => IsLoggedIn);
        SubmitCommand = Command(() => Submit(false), () => CanControl && SelectedRole is not null && !HasPending);
        RetryCommand = Command(SendPending, () => _connected && HasPending);
        CancelCommand = Command(async () => { await Client.Post<Job>("/api/jobs/cancel", new JobActionRequest(Generation, SelectedJob!.Id)); Message = "선택 취소 요청을 처리했습니다. 전송된 동작의 물리 정지·롤백은 아닙니다."; },
            () => CanControl && SelectedJob is not null && (SelectedJob.Job.Active || SelectedJob.Job.Status == JobStatus.NeedsReview));
        ManualSwitchCommand = Command(async () =>
        {
            if (!ConfirmManualSwitch($"'{SelectedJob!.Name}' 전체의 후속 단계를 중단합니다.\n전송된 동작은 되돌리지 않습니다. 대상 가상 상태를 대조한 후 새 수동 명령을 선택하세요.\n\n대상: {SelectedJob.Targets}")) return;
            await Client.Post<Job>("/api/jobs/manual-switch", new JobActionRequest(Generation, SelectedJob.Id));
            Message = "시나리오 후속 단계를 차단했습니다. 처리 종료 후 대상별 '가상 상태 조회·대조'를 수행하고 새 명령을 선택하세요.";
        }, () => CanControl && SelectedJob?.Job.Kind == JobKind.Scenario && SelectedJob.Job.Active);
        ReconcileCommand = Command(async () => { await Client.Post<DeviceState>("/api/devices/reconcile", new ReconcileRequest(Generation, SelectedDevice!.Id)); Message = "가상 상태 대조 완료. 과거 불확실 명령의 이력은 그대로 유지됩니다."; }, () => CanControl && SelectedDevice is not null);
        SaveDeviceCommand = Command(SaveDevice, () => CanConfigure);
        LoadDeviceCommand = Command(() => { LoadDevice(); return Task.CompletedTask; }, () => SelectedDevice is not null);
        NewDeviceCommand = Command(() => { DeviceIdText = Guid.NewGuid().ToString(); DeviceExpectedVersion = 0; DeviceName = ""; NotifyEditors(); return Task.CompletedTask; });
        SaveRoleCommand = Command(async () =>
        {
            var target = SelectedDevice!;
            var roleId = RoleName.Trim();
            var version = _state?.Roles.SingleOrDefault(r => r.Id == roleId)?.Version ?? 0;
            var saved = await Client.Post<RoleBinding>("/api/roles", new RoleRequest(Generation, roleId, target.Id, version));
            await Refresh();
            SelectedRole = Roles.SingleOrDefault(r => r.Id == saved.Id);
            Message = $"역할 배정 완료: {saved.Id} → {target.Name} / {target.PcName}. 장비 제어에서 기능·값을 선택해 명령을 접수하세요.";
        }, () => CanConfigure && SelectedDevice is not null && !string.IsNullOrWhiteSpace(RoleName));
        AddStepCommand = Command(() =>
        {
            if (SelectedRole is null || SelectedCapability is null) throw new ArgumentException("장비 제어 탭에서 역할과 기능을 선택하세요.");
            DeviceOperation? condition = string.IsNullOrWhiteSpace(ConditionOperationText) ? null : Enum.Parse<DeviceOperation>(ConditionOperationText, true);
            int? expected = string.IsNullOrWhiteSpace(ConditionValueText) ? null : int.Parse(ConditionValueText);
            DraftSteps.Add(new(SelectedRole.Id, SelectedCapability.Operation, CommandValue, DelayMs, TimeoutMs, DraftFailurePolicy, condition, expected));
            return Task.CompletedTask;
        });
        RemoveStepCommand = Command(() => { if (SelectedDraftStep is not null) DraftSteps.Remove(SelectedDraftStep); return Task.CompletedTask; });
        SaveScenarioCommand = Command(async () =>
        {
            var saved = await Client.Post<ScenarioDefinition>("/api/scenarios", new ScenarioRequest(Generation, _scenarioId, ScenarioName, DraftSteps.ToArray(), _scenarioVersion));
            _scenarioVersion = saved.Version; Message = "시나리오 정의를 저장했습니다.";
        }, () => CanConfigure);
        LoadScenarioCommand = Command(() =>
        {
            if (SelectedScenario is null) return Task.CompletedTask;
            _scenarioId = SelectedScenario.Id; _scenarioVersion = SelectedScenario.Version; ScenarioName = SelectedScenario.Name;
            DraftSteps.Clear(); foreach (var step in SelectedScenario.Steps) DraftSteps.Add(step);
            Changed(nameof(ScenarioName)); return Task.CompletedTask;
        });
        NewScenarioCommand = Command(() => { _scenarioId = Guid.NewGuid(); _scenarioVersion = 0; ScenarioName = ""; DraftSteps.Clear(); Changed(nameof(ScenarioName)); return Task.CompletedTask; });
        RunScenarioCommand = Command(() => Submit(true), () => CanControl && !HasPending);
        CreateAccountCommand = Command(async () =>
        {
            var password = ReadNewPassword();
            try { await Client.Post<AccountView>("/api/accounts", new CreateAccountRequest(Generation, NewAccountName, password, NewAccountRole, AccountAllDevices, ParseScope())); }
            finally { ClearNewPassword(); }
            Message = "앱 계정을 등록했습니다.";
        }, () => CanConfigure);
        LoadAccountCommand = Command(() =>
        {
            if (SelectedAccount is null) return Task.CompletedTask;
            AccountEnabled = SelectedAccount.Enabled; AccountAllDevices = SelectedAccount.AllDevices;
            AccountDeviceIds = string.Join(",", SelectedAccount.DeviceIds); NotifyEditors(); return Task.CompletedTask;
        });
        UpdateAccountCommand = Command(async () =>
        {
            if (SelectedAccount is null) throw new ArgumentException("권한을 변경할 계정을 선택하세요.");
            await Client.Post<bool>("/api/accounts/permissions", new UpdateAccountRequest(Generation, SelectedAccount.Id, AccountEnabled, AccountAllDevices, ParseScope()));
            Message = "계정 권한을 반영했습니다. 접수 작업도 다음 전송 전에 현재 권한을 재검증합니다.";
        }, () => CanConfigure);
        ReviewCommand = Command(async () =>
        {
            _review = await Client.Post<RecoveryReview>("/api/recovery/review");
            ReviewText = $"확인 시각: {_review.ReviewedAt:O}\n사용권 세대: {_review.Generation}\n불확실 장비: {string.Join(", ", _review.UncertainDevices)}\n\n" +
                string.Join("\n\n", _review.Jobs.Select(j => $"{j.Id} | {j.Snapshot.RequesterName} | {j.Snapshot.Name}\n{j.Status} | {j.Result}\n" +
                    string.Join(", ", j.Snapshot.Steps.Select(x => $"{x.Target.PcName}/{x.Target.Name}").Distinct())));
            ReviewText += "\n\nHiperwall 편집 · 진행/결과 확인 필요\n" + string.Join("\n\n", _review.HiperwallEdits.Select(r =>
                $"{r.Requester.UserName} / {r.Requester.PcName} · {r.Summary}\n요청 {r.Request.RequestId}\n" + string.Join("\n", r.Steps.Select(s => $"{s.Command.InstanceId}: {s.Message}"))));
            Changed(nameof(ReviewText)); Message = "진행 작업과 불확실 대상을 확인한 후 관리자 복구 인계를 승인하세요.";
        }, () => IsAdmin && _state?.Lease.Mode == LeaseMode.RecoveryRequired);
        ApproveCommand = Command(async () =>
        {
            await Client.Post<Lease>("/api/recovery/approve", new RecoveryApprovalRequest(_review!.ReviewId));
            _review = null; Message = "관리자 복구 인계 완료. 다음 운영자는 사용 시작 후 남은 작업을 검토하세요.";
        }, () => IsAdmin && _review is not null && _state?.Lease.Mode == LeaseMode.RecoveryRequired);
        InitializeLighting();
        InitializeHandover();
        InitializeStorage();
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
    }
    private Guid[] ParseScope() => AccountDeviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToArray();
    private AsyncCommand Command(Func<Task> action, Func<bool>? available = null)
    {
        var command = new AsyncCommand(async () =>
        {
            _busy = true; Notify();
            try { await action(); if (IsLoggedIn) await Refresh(); }
            catch (Exception error) { Report(error); }
            finally { _busy = false; Notify(); }
        }, () => !_busy && !_closing && (available?.Invoke() ?? true));
        _commands.Add(command); return command;
    }
    private void Report(Exception error)
    {
        if (error is ApiException { Code: "login_failed" } loginError) Message = loginError.Message;
        else if (error is ApiException api && (api.Status == HttpStatusCode.Unauthorized || api.Code == "account_disabled"))
        { _connected = false; _login = null; _state = null; _review = null; Message = "인증 세션이 만료되었거나 계정이 차단되었습니다. 다시 로그인하세요."; }
        else if (error is HttpRequestException or TaskCanceledException || error is ApiException { Status: HttpStatusCode.ServiceUnavailable }) { _connected = false; Message = "연결 이상: 신규 제어 차단. 호스트 연결과 관리자 복구 상태를 확인하세요."; }
        else Message = error.Message;
        Notify();
    }
    private async Task Login()
    {
        var password = ReadLoginPassword();
        try { _client?.Dispose(); _client = new(Endpoint, Fingerprint); _login = await Client.Post<LoginResult>("/api/login", new LoginRequest(LoginName, password, _preferences.PcId, Environment.MachineName)); }
        finally { ClearLoginPassword(); }
        Client.SetToken(_login.Token); _state = null; _connected = true;
        (_preferences with { Endpoint = Endpoint, Fingerprint = Fingerprint }).Save();
        Message = "로그인 완료. 조회 모드에서 현황을 확인하고 사용 시작을 선택하세요.";
    }
    private async Task Logout()
    {
        Hiperwall.Close();
        try { await Client.Post<bool>("/api/logout"); Message = "로그아웃 완료. 접수 작업은 호스트에서 계속 처리합니다."; }
        finally
        {
            _login = null; _connected = false; _state = null; _review = null;
            _pending = null; _pendingLightBatch = null; _editingLightOrder = false; Lights.Clear(); Devices.Clear(); Roles.Clear(); Jobs.Clear(); Scenarios.Clear(); Accounts.Clear(); Audit.Clear();
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
        if (_login?.Session.Id != sessionId || _login is null || (_state is not null && state.Revision < _state.Revision)) return;
        _state = state; _connected = true;
        var selectedDeviceId = _selectedDevice?.Id; var selectedRoleId = _selectedRole?.Id;
        var selectedJobId = _selectedJob?.Id; var scenarioId = SelectedScenario?.Id;
        var selectedAccountId = SelectedAccount?.Id; var modelId = SelectedModel?.Id; var operation = SelectedCapability?.Operation;
        // Keep row identity so polling does not reset selection or an open target picker.
        foreach (var removed in Devices.Where(row => state.Devices.All(d => d.Id != row.Id)).ToArray()) Devices.Remove(removed);
        foreach (var d in state.Devices)
        {
            var value = state.DeviceStates[d.Id];
            var reserved = state.Jobs.Any(j => j.Active && j.Kind == JobKind.Scenario && j.Snapshot.Steps.Any(x => x.Target.Id == d.Id));
            var row = new DeviceRow(d, string.Join(" / ", value.Simulated.Select(x => $"{x.Key}={x.Value.Value} (가상 · {x.Value.At.ToLocalTime():HH:mm:ss})").Concat(
                    value.Observed.Count == 0 ? [] : new[] { DeviceEvidence.Observations(value, DateTimeOffset.UtcNow) })),
                string.Join(" / ", value.Desired.Select(x => $"{x.Key}={x.Value}")), value.Connection, value.LastCommand is { } evidence ? $"{DeviceEvidence.Label(evidence.Confirmation)} · {value.LastResult}" : value.LastResult,
                state.UncertainDevices.Contains(d.Id) ? "대조 필요" : reserved ? "시나리오 예약" : "사용 가능");
            var existing = Devices.SingleOrDefault(x => x.Id == d.Id);
            if (existing is null) Devices.Add(row); else existing.Update(row);
        }
        _selectedDevice = Devices.FirstOrDefault(x => x.Id == selectedDeviceId);
        Replace(Roles, state.Roles); _selectedRole = Roles.FirstOrDefault(x => x.Id == selectedRoleId);
        Replace(Models, state.Models); SelectedModel = Models.FirstOrDefault(x => x.Id == modelId) ?? Models.FirstOrDefault();
        Replace(Scenarios, state.Scenarios); SelectedScenario = Scenarios.FirstOrDefault(x => x.Id == scenarioId);
        Replace(Accounts, state.Accounts); SelectedAccount = Accounts.FirstOrDefault(x => x.Id == selectedAccountId);
        Replace(Jobs, state.Jobs.OrderByDescending(j => j.Snapshot.AcceptedAt).Select(j => new JobRow(j, j.Snapshot.SessionId != _login?.Session.Id)));
        _selectedJob = Jobs.FirstOrDefault(x => x.Id == selectedJobId);
        var auditEntry = SelectedAudit?.Entry;
        Replace(Audit, state.Audit.Reverse().Select(a => new AuditRow(a)));
        SelectedAudit = Audit.FirstOrDefault(a => a.Entry == auditEntry);
        if (HasPending && state.Jobs.Any(j => j.Snapshot.RequestId == (_pending?.RequestId ?? _pendingLightBatch?.RequestId)))
        { _pending = null; _pendingLightBatch = null; Message = "요청 ID로 호스트 접수 기록을 확인했습니다."; }
        Changed(nameof(SelectedDevice)); Changed(nameof(SelectedRole)); Changed(nameof(SelectedJob));
        Changed(nameof(SelectedScenario)); Changed(nameof(SelectedModel)); Changed(nameof(SelectedAccount));
        _selectedCapability = Capabilities.FirstOrDefault(c => c.Operation == operation); Changed(nameof(SelectedCapability)); Changed(nameof(Capabilities)); Changed(nameof(JobDetails)); Notify();
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var list = values.ToArray();
        if (JsonSerializer.Serialize(target, JsonDefaults.Options) == JsonSerializer.Serialize(list, JsonDefaults.Options)) return;
        target.Clear(); foreach (var value in list) target.Add(value);
    }
    private async Task Submit(bool scenario)
    {
        if (scenario && SelectedScenario is null) throw new ArgumentException("실행할 시나리오를 선택하세요.");
        if (!scenario && SelectedCapability is null) throw new ArgumentException("지원 기능을 선택하세요.");
        _pending = new(Guid.NewGuid(), Generation, scenario ? null : SelectedRole!.Id,
            SelectedCapability?.Operation ?? DeviceOperation.Power, CommandValue, scenario ? SelectedScenario!.Id : null, DelayMs, TimeoutMs);
        Notify(); await SendPending();
    }
    private async Task SendPending()
    {
        if (!HasPending) return;
        try
        {
            var job = _pendingLightBatch is not null ? await Client.Post<Job>("/api/lights/power", _pendingLightBatch) : await Client.Post<Job>("/api/jobs", _pending);
            _pending = null; _pendingLightBatch = null; Message = $"접수 완료: {job.Id}. 실행 결과는 작업 탭에서 확인하세요.";
        }
        catch (ApiException error) when ((int)error.Status < 500) { _pending = null; _pendingLightBatch = null; throw; }
        finally { Notify(); }
    }
    private async Task SaveDevice()
    {
        if (SelectedModel is null) throw new ArgumentException("가상 모델을 선택하세요.");
        var device = await Client.Post<DeviceConfig>("/api/devices", new DeviceRequest(Generation, Guid.Parse(DeviceIdText),
            Guid.Parse(PcIdText), PcName, DeviceName, ConnectionId, SelectedModel.Id, DeviceEnabled, DeviceFault, DeviceLatencyMs, DeviceExpectedVersion));
        DeviceExpectedVersion = device.Version; Message = "가상 장비 설정을 저장했습니다."; NotifyEditors();
    }
    private void LoadDevice()
    {
        var d = SelectedDevice!.Config;
        DeviceIdText = d.Id.ToString(); PcIdText = d.PcId.ToString(); PcName = d.PcName; DeviceName = d.Name;
        ConnectionId = d.ConnectionId; SelectedModel = Models.Single(x => x.Id == d.ModelId); DeviceEnabled = d.Enabled;
        DeviceFault = d.Fault; DeviceLatencyMs = d.LatencyMs; DeviceExpectedVersion = d.Version; NotifyEditors();
    }
    private void NotifyEditors()
    {
        foreach (var name in new[] { nameof(DeviceIdText), nameof(PcIdText), nameof(PcName), nameof(DeviceName), nameof(ConnectionId),
            nameof(SelectedModel), nameof(DeviceEnabled), nameof(DeviceFault), nameof(DeviceLatencyMs), nameof(DeviceExpectedVersion),
            nameof(AccountAllDevices), nameof(AccountDeviceIds), nameof(AccountEnabled) }) Changed(name);
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanEditLogin), nameof(IsLoggedIn), nameof(IsAdmin), nameof(CanControl), nameof(CanConfigure), nameof(SiteTitle),
            nameof(UserSummary), nameof(LeaseSummary), nameof(PreviousSummary), nameof(ConnectionSummary), nameof(PendingSummary),
            nameof(RecoverySummary), nameof(RoleTargetSummary), nameof(RoleAssignmentHint) }) Changed(name);
        RefreshLighting();
        RefreshHandover();
        RefreshStorageContext();
        Hiperwall.Generation = Generation;
        Hiperwall.UpdateContext(_connected && !_closing ? _client : null, _connected && !_closing ? _login?.Session.Id : null,
            IsAdmin, CanConfigure, _state?.HiperwallReadSupported ?? false, _state?.HiperwallConfigurationVersion ?? 0, CanControl && (_state?.CanControlHiperwall ?? false), _state?.HiperwallWriteSupported ?? false);
        foreach (var command in _commands) command.Raise();
    }
    public async Task CloseAsync()
    {
        _closing = true; Hiperwall.Close(); _timer.Stop(); Notify();
        try { if (IsLoggedIn) await Client.Post<bool>("/api/logout"); }
        catch (Exception) { /* The host fences on missed heartbeat; accepted work remains authoritative. */ }
        finally { _client?.Dispose(); }
    }
}
