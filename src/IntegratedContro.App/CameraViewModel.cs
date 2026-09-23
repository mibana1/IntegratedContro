using CameraSnapshot = IntegratedContro.Core.CameraView;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class CameraViewModel : Bindable
{
    private HostClient? _client;
    private Guid? _session;
    private bool _configure, _supported, _busy, _refreshing, _visible, _updatingList, _connected;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private CancellationTokenSource? _backgroundRefreshCancellation;
    private long _epoch;
    private CancellationTokenSource _context = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _videoGate = new(1, 1);
    private readonly IHiperwallContentLookup _contentLookup;
    private IVideoPresentation? _presentation;
    private IVideoPlayer? _player => _presentation?.Player;
    private LoopbackVideoRelay? _relay;
    private CancellationTokenSource? _playback;
    private Action<VideoPlaybackStatus>? _playbackStatus;
    private long _playEpoch;
    private int _mediaVersion = -1;
    private Guid _draftId = Guid.NewGuid();
    private int _draftVersion;
    private MediaSettingsView _settings = new(0, "", "", "", "", false);
    private CameraSnapshot? _editing;
    private readonly Dictionary<Guid, (int Version, bool IsNew)> _pendingRegistrations = [];
    public event Action<string>? StatusReported;
    private readonly List<AsyncCommand> _commands = [];
    public long Generation { get; set; }
    public bool IsBusy => _busy || _refreshing;
    // Only explicit operations lock the controls; catalog polling must not interrupt interaction.
    public bool CanConfigure => _connected && _configure && !_busy && _client is not null;
    public bool CanPlay => !HasLocalMediaChange && _connected && _visible && _client is not null && Selected is { Enabled: true, Provisioning: CameraProvisioning.Ready };
    public bool IsPlaying => _player is not null;
    public Func<CancellationToken, Task<IVideoPresentation>> PlayerFactory { get; set; }
    public long DecodedFrames => _presentation?.DecodedFrames ?? 0;
    public object? VideoSurface => _presentation?.Surface;
    private string _message = "로그인 후 카메라 목록을 조회하세요.";
    public string Message { get => _message; private set => Set(ref _message, value); }
    private void PublishStatus(string message)
    {
        Message = message;
        StatusReported?.Invoke(message);
    }
    private void PublishCameraSuccess(CameraSnapshot camera, bool isNew)
    {
        var detail = camera.Provisioning switch
        {
            CameraProvisioning.Ready => "영상 경로 준비 완료 · 재생으로 실제 영상을 확인하세요.",
            CameraProvisioning.Disabled => "비활성 상태로 저장되었습니다.",
            _ => "영상 경로 준비 중"
        };
        PublishStatus($"{(isNew ? "카메라 등록 성공" : "카메라 수정 성공")}: '{camera.Name}' · {detail}");
    }
    private void PublishCameraFailure(CameraSnapshot camera) =>
        PublishStatus($"카메라 '{camera.Name}' {(camera.Provisioning == CameraProvisioning.DeleteFailed ? "삭제 실패" : "경로 준비 실패")}: {camera.Message}");
    private string _playbackMessage = "카메라를 선택하고 재생을 누르세요.";
    public string PlaybackMessage { get => _playbackMessage; private set => Set(ref _playbackMessage, value); }
    public string CleanupSummary { get; private set; } = "경로 정리 대기 0건";
    public string DraftSummary => $"카메라 {_draftId.ToString()[..8]} · 저장 버전 {_draftVersion} · RTSP 빈 입력은 기존 보호 정보 유지";
    public ObservableCollection<CameraSnapshot> Cameras { get; } = [];
    public ObservableCollection<CameraSnapshot> FilteredCameras { get; } = [];
    public ReadOnlyObservableCollection<HiperwallItemRow> MappingContents => _contentLookup.Contents;
    private HiperwallItemRow? _selectedMapping;
    private bool _clearMapping;
    public HiperwallItemRow? SelectedMapping { get => _selectedMapping; set => Set(ref _selectedMapping, value); }
    public bool ClearMapping { get => _clearMapping; set => Set(ref _clearMapping, value); }
    private CameraSnapshot? _selected;
    public CameraSnapshot? Selected
    {
        get => _selected;
        set
        {
            if (_updatingList || Equals(_selected, value)) return;
            if (_selected?.Id != value?.Id || _selected?.Version != value?.Version || value?.Provisioning != CameraProvisioning.Ready) { _playEpoch++; _ = StopPlaybackAsync(); }
            var failureChanged = value is { Provisioning: CameraProvisioning.Failed or CameraProvisioning.DeleteFailed } &&
                (_selected?.Id != value.Id || _selected.Version != value.Version ||
                 _selected.Provisioning != value.Provisioning || _selected.Message != value.Message);
            Set(ref _selected, value); Changed(nameof(SelectedDetails)); Raise();
            if (failureChanged) PublishCameraFailure(value!);
        }
    }
    public string SelectedDetails => Selected is null ? "등록 카메라를 선택하세요." :
        $"{Selected.Name} · {Selected.Location}\n{Selected.StateLabel} · {Selected.Message}\n경로: {Selected.StreamPath}\n" +
        $"Hiperwall 매핑: {Selected.ContentSelector ?? "없음"} {Selected.ContentValue}\n등록과 Hiperwall Source는 별개입니다.";
    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    private string _cameraName = "", _location = "", _apiEndpoint = "", _hlsEndpoint = "", _apiUser = "", _hlsUser = "";
    private bool _enabled = true, _muted = true;
    public string CameraName { get => _cameraName; set => Set(ref _cameraName, value); }
    public string Location { get => _location; set => Set(ref _location, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string ApiEndpoint { get => _apiEndpoint; set => Set(ref _apiEndpoint, value); }
    public string HlsEndpoint { get => _hlsEndpoint; set => Set(ref _hlsEndpoint, value); }
    public string ApiUser { get => _apiUser; set => Set(ref _apiUser, value); }
    public string HlsUser { get => _hlsUser; set => Set(ref _hlsUser, value); }
    public bool Muted { get => _muted; set { if (Set(ref _muted, value) && _player is not null) _player.Muted = value; } }
    public Func<string> ReadRtsp { get; set; } = () => "";
    public Func<string> ReadRtspUser { get; set; } = () => "";
    public Func<string> ReadRtspPassword { get; set; } = () => "";
    public Func<string> ReadApiPassword { get; set; } = () => "";
    public Func<string> ReadHlsPassword { get; set; } = () => "";
    public Action ClearSecrets { get; set; } = () => { };
    public Func<string, bool> ConfirmForceDelete { get; set; } = _ => false;
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SyncCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand ForceDeleteCommand { get; }
    public AsyncCommand RetryCleanupCommand { get; }
    public AsyncCommand PlayCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand StatusCommand { get; }
    public CameraViewModel(IHiperwallContentLookup contentLookup, Func<CancellationToken, Task<IVideoPresentation>> playerFactory)
    {
        _contentLookup = contentLookup; PlayerFactory = playerFactory;
        RefreshCommand = Command(Refresh, () => _connected && _client is not null && _supported);
        NewCommand = Command(_ => { NewDraft(); return Task.CompletedTask; }, () => CanConfigure);
        LoadCommand = Command(_ =>
        {
            var c = Selected!; _editing = c; _draftId = c.Id; _draftVersion = c.Version;
            CameraName = c.Name; Location = c.Location; Enabled = c.Enabled;
            SelectedMapping = null; ClearMapping = false; ClearSecrets(); Changed(nameof(DraftSummary)); return Task.CompletedTask;
        }, () => CanConfigure && Selected is not null);
        SaveCommand = Command(async ct =>
        {
            try
            {
                var selector = ClearMapping ? null : SelectedMapping is { } map ? map.Item.Id is null ? "name" : "uuid" : _editing?.ContentSelector;
                var value = ClearMapping ? null : SelectedMapping is { } content ? content.Item.Id ?? content.Item.Name : _editing?.ContentValue;
                var isNew = _draftVersion == 0;
                var result = await _client!.Post<CameraSnapshot>("/api/cameras/save",
                    new SaveCameraRequest(Generation, _draftId, _draftVersion, CameraName, Location, Enabled,
                        ReadRtsp(), ReadRtspUser(), ReadRtspPassword(), selector, value, _contentLookup.ConfigurationVersion), ct);
                ct.ThrowIfCancellationRequested();
                _draftVersion = result.Version; _editing = result; Changed(nameof(DraftSummary));
                _pendingRegistrations[result.Id] = (result.Version, isNew);
                PublishCameraSuccess(result, isNew);
                await Refresh(ct);
            }
            finally { if (!ct.IsCancellationRequested) ClearSecrets(); }
        }, () => CanConfigure, "카메라 저장 오류");
        InitializeMediaCommands();
        SyncCommand = Command(async ct =>
        {
            await _client!.Post<bool>("/api/cameras/sync", Action(), ct); ct.ThrowIfCancellationRequested(); PublishStatus("재동기화 접수 완료."); await Refresh(ct);
        }, () => CanConfigure && Selected is not null);
        DeleteCommand = Command(ct => Delete(false, ct), () => CanConfigure && Selected is not null);
        ForceDeleteCommand = Command(ct => Delete(true, ct), () => CanConfigure && Selected is not null);
        RetryCleanupCommand = Command(async ct =>
        {
            await _client!.Post<bool>("/api/cameras/cleanup", new LeaseRequest(Generation), ct);
            ct.ThrowIfCancellationRequested(); PublishStatus("정리 큐 재시도를 접수했습니다."); await Refresh(ct);
        }, () => CanConfigure);
        StatusCommand = Command(async ct =>
        {
            var c = Selected!;
            var status = await _client!.Get<CameraConnection>($"/api/cameras/{c.Id}/status?version={c.Version}", ct);
            if (!ct.IsCancellationRequested && Selected?.Id == c.Id && Selected.Version == c.Version) PublishStatus(status.Message);
        }, () => CanPlay);
        PlayCommand = Command(StartPlayback, () => CanPlay);
        StopCommand = new(StopPlaybackAsync);
        _timer.Tick += async (_, _) => { if (_connected && !_busy && _visible && _client is not null && _supported) await Run(Refresh, backgroundRefresh: true); };
    }
    private CameraActionRequest Action(bool force = false) => new(Generation, Selected!.Id, Selected.Version, force);
    private async Task Delete(bool force, CancellationToken ct)
    {
        var request = Action(force);
        if (force && !ConfirmForceDelete($"'{Selected!.Name}' 등록을 강제 삭제하고 경로를 정리 큐에 남깁니다.\n{Selected.StreamPath}\n경로가 실제 삭제될 때까지 영상 서버에 남을 수 있습니다.")) return;
        _playEpoch++; await StopPlaybackAsync(); ct.ThrowIfCancellationRequested();
        await _client!.Post<bool>("/api/cameras/delete", request, ct);
        ct.ThrowIfCancellationRequested();
        PublishStatus(force ? "등록 제거 완료 · 경로 정리 큐를 확인하세요." : "삭제 접수 · 경로 삭제 실패 시 등록이 유지됩니다.");
        await Refresh(ct);
    }
    private void NewDraft()
    {
        _draftId = Guid.NewGuid(); _draftVersion = 0; _editing = null; CameraName = ""; Location = "";
        Enabled = true; SelectedMapping = null; ClearMapping = false; ClearSecrets(); Changed(nameof(DraftSummary));
    }
    private AsyncCommand Command(Func<CancellationToken, Task> action, Func<bool> enabled, string failureTitle = "카메라 작업 오류")
    {
        var command = new AsyncCommand(() => Run(action, failureTitle: failureTitle), () => !_busy && enabled()); _commands.Add(command); return command;
    }
    private async Task Run(Func<CancellationToken, Task> action, bool backgroundRefresh = false, string failureTitle = "카메라 목록 조회 오류")
    {
        if (!_connected || _busy || (backgroundRefresh && _refreshing)) return;
        var epoch = _epoch;
        using var refreshCancellation = backgroundRefresh ? CancellationTokenSource.CreateLinkedTokenSource(_context.Token) : null;
        var ct = refreshCancellation?.Token ?? _context.Token;
        if (backgroundRefresh) { _refreshing = true; _backgroundRefreshCancellation = refreshCancellation; }
        else
        {
            _busy = true;
            // Give an accepted click priority over a read, without overlapping requests or applying its stale response.
            _backgroundRefreshCancellation?.Cancel();
        }
        Raise();
        try
        {
            await _requestGate.WaitAsync(ct);
            try { ct.ThrowIfCancellationRequested(); await action(ct); }
            finally { _requestGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (epoch == _epoch)
            {
                // Deliberate navigation/click cancellation is silent; an HTTP deadline is an actionable failure.
                var reason = e switch
                {
                    ApiException or DomainException => e.Message,
                    OperationCanceledException or TimeoutException =>
                        "호스트 응답 시간이 초과되어 처리 결과를 확인하지 못했습니다. 목록을 새로 조회해 등록 여부를 확인하세요.",
                    HttpRequestException => "호스트에 연결할 수 없습니다. 서버 실행 상태와 네트워크 연결을 확인하세요.",
                    _ => "영상 호스트 연결 또는 입력·엔진 설정을 확인하세요."
                };
                PublishStatus($"{failureTitle}: {reason}");
            }
        }
        finally
        {
            if (ReferenceEquals(_backgroundRefreshCancellation, refreshCancellation)) _backgroundRefreshCancellation = null;
            if (epoch == _epoch)
            {
                if (backgroundRefresh) _refreshing = false; else _busy = false;
                Raise();
            }
        }
    }
    public void UpdateContext(HostClient? client, Guid? session, bool configure, bool supported, int mediaVersion, bool connected = true)
    {
        var connectionChanged = _connected != (connected && client is not null && session is not null);
        _connected = connected && client is not null && session is not null;
        _configure = configure; _supported = supported;
        var sessionChanged = !ReferenceEquals(client, _client) || session != _session;
        if (sessionChanged)
        {
            CancelRequests();
            _playEpoch++; _ = StopPlaybackAsync();
            _client = client; _session = session; ResetMediaSettings();
            _pendingRegistrations.Clear();
            Cameras.Clear(); FilteredCameras.Clear(); Selected = null; NewDraft();
            CleanupSummary = "경로 정리 대기 0건"; Changed(nameof(CleanupSummary)); Changed(nameof(AppliedMedia));
            Message = client is null ? "로그인 후 카메라 목록을 조회하세요." : "호스트의 영상 설정과 카메라 목록을 자동으로 불러옵니다.";
            if (_connected && _visible && supported) _ = Run(Refresh, backgroundRefresh: true);
        }
        else if (connectionChanged && !_connected)
        {
            // Suspend network work without replacing the authenticated session or its local draft.
            CancelRequests(); _playEpoch++; _ = StopPlaybackAsync();
        }
        if (connectionChanged && !sessionChanged && session is not null)
            Message = _connected ? "호스트 연결 복구 · 작성 중인 카메라 입력을 유지했습니다." :
                "호스트 연결 끊김 · 작성 중인 카메라 입력은 유지되며 연결 복구 전까지 전송할 수 없습니다.";
        if (_mediaVersion != mediaVersion) { _mediaVersion = mediaVersion; _playEpoch++; _ = StopPlaybackAsync(); }
        Raise();
    }
    private void CancelRequests()
    {
        _epoch++; _context.Cancel(); _context.Dispose(); _context = new(); _busy = false; _refreshing = false;
    }
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) { _timer.Start(); if (_connected && _client is not null && !_busy && _supported) _ = Run(Refresh, backgroundRefresh: true); }
        else
        {
            _timer.Stop(); CancelRequests();
            _playEpoch++; _ = StopPlaybackAsync(); ClearSecrets();
        }
        Raise();
    }
    public async Task CloseAsync()
    {
        SetVisible(false); await StopPlaybackAsync(); ClearSecrets();
    }
    private async Task Refresh(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var epoch = _epoch;
        CameraCatalog result;
        try { result = await _client!.Get<CameraCatalog>("/api/cameras", ct); }
        catch
        {
            if (epoch == _epoch && !ct.IsCancellationRequested) { _mediaReadFailed = true; Raise(); }
            throw;
        }
        if (epoch != _epoch || ct.IsCancellationRequested) return;
        if (result.Settings.LocalServer?.ChangeId is not null && result.Settings.LocalServer.ChangeId != _settings.LocalServer?.ChangeId)
        { _playEpoch++; _ = StopPlaybackAsync(); }
        ApplyMediaSettings(result.Settings); Raise();
        MergeCameras(Cameras, result.Cameras);
        Filter();
        foreach (var pending in _pendingRegistrations.ToArray())
        {
            var camera = result.Cameras.FirstOrDefault(c => c.Id == pending.Key);
            if (camera is null || camera.Version != pending.Value.Version) { _pendingRegistrations.Remove(pending.Key); continue; }
            if (camera.Provisioning is CameraProvisioning.Pending or CameraProvisioning.Deleting) continue;
            _pendingRegistrations.Remove(pending.Key);
            if (camera.Provisioning is CameraProvisioning.Failed or CameraProvisioning.DeleteFailed) PublishCameraFailure(camera);
            else PublishCameraSuccess(camera, pending.Value.IsNew);
        }
        CleanupSummary = $"경로 정리 대기 {result.Cleanup.Length}건" +
            string.Join("", result.Cleanup.Select(c => $"\n{c.StreamPath} · 요청자 {c.RequesterName} · {c.Attempts}회 · {c.Message}"));
        Changed(nameof(CleanupSummary)); Changed(nameof(AppliedMedia));
    }
    private void Filter()
    {
        var selectedId = Selected?.Id;
        _updatingList = true;
        try
        {
            MergeCameras(FilteredCameras, Cameras.Where(c =>
                (c.Name + " " + c.Location).Contains(Search, StringComparison.CurrentCultureIgnoreCase)).ToArray());
        }
        finally { _updatingList = false; }
        Selected = FilteredCameras.FirstOrDefault(c => c.Id == selectedId);
    }
    private static void MergeCameras(ObservableCollection<CameraSnapshot> target, CameraSnapshot[] values)
    {
        var ids = values.Select(c => c.Id).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!ids.Contains(target[i].Id)) target.RemoveAt(i);
        for (var i = 0; i < values.Length; i++)
        {
            var existing = target.FirstOrDefault(c => c.Id == values[i].Id);
            if (existing is null) target.Insert(i, values[i]);
            else
            {
                var index = target.IndexOf(existing);
                if (index != i) target.Move(index, i);
                if (existing != values[i]) target[i] = values[i];
            }
        }
    }
    private async Task StartPlayback(CancellationToken ct)
    {
        var camera = Selected!; var client = _client!;
        var epoch = ++_playEpoch;
        await _videoGate.WaitAsync(ct);
        try
        {
            await ClearPlayer();
            if (!_visible || epoch != _playEpoch || ct.IsCancellationRequested) return;
            _playback = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _relay = new LoopbackVideoRelay((asset, token) => client.GetMedia(camera.Id, camera.Version, asset, token));
            try { _presentation = await PlayerFactory(_playback.Token); }
            catch (OperationCanceledException) when (_playback.IsCancellationRequested) { await ClearPlayer(); return; }
            catch { await ClearPlayer(); PlaybackMessage = "영상 엔진을 초기화하지 못했습니다. 설치 파일과 실행 환경을 확인하세요."; return; }
            if (!_visible || epoch != _playEpoch || _playback.IsCancellationRequested) { await ClearPlayer(); return; }
            var presentation = _presentation; var player = presentation.Player;
            player.Muted = Muted; Changed(nameof(VideoSurface)); Changed(nameof(IsPlaying));
            _playbackStatus = status => _dispatcher.BeginInvoke(() =>
            { if (ReferenceEquals(_player, player) && epoch == _playEpoch) PlaybackMessage = status.Message; });
            player.StatusChanged += _playbackStatus;
            Task task;
            try { task = player.PlayAsync(_relay.Source, _playback.Token); }
            catch { await ClearPlayer(); PlaybackMessage = "영상 재생을 시작하지 못했습니다. 실행 환경을 확인하세요."; return; }
            _ = PlaybackEnded(task, presentation, epoch);
        }
        finally { _videoGate.Release(); Raise(); }
    }
    private async Task PlaybackEnded(Task task, IVideoPresentation presentation, long epoch)
    {
        var failed = false;
        try { await task; }
        catch (OperationCanceledException) { }
        catch { failed = true; }
        if (ReferenceEquals(_presentation, presentation) && _playEpoch == epoch)
        {
            await _videoGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_presentation, presentation)) { PlaybackMessage = failed ? "영상 재생에 실패했습니다. 연결과 실행 환경을 확인하세요." : presentation.LastStatus.Message; await ClearPlayer(); }
            }
            finally { _videoGate.Release(); Raise(); }
        }
    }
    public async Task StopPlaybackAsync()
    {
        _playback?.Cancel();
        await _videoGate.WaitAsync();
        try { await ClearPlayer(); PlaybackMessage = "재생 중지 · 영상 자원 정리 완료"; }
        finally { _videoGate.Release(); Raise(); }
    }
    private async Task ClearPlayer()
    {
        _playback?.Cancel();
        var presentation = _presentation; _presentation = null; Changed(nameof(VideoSurface)); Changed(nameof(IsPlaying));
        if (presentation is not null && _playbackStatus is not null)
            presentation.Player.StatusChanged -= _playbackStatus;
        _playbackStatus = null;
        var relay = _relay; _relay = null;
        var playback = _playback; _playback = null;
        // Abort network reads before joining the engine, releasing all resources even if one adapter fails.
        try { if (relay is not null) await relay.DisposeAsync(); }
        finally
        {
            try { if (presentation is not null) await presentation.DisposeAsync(); }
            finally { playback?.Dispose(); }
        }
    }
    private void Raise()
    {
        Changed(nameof(IsBusy)); Changed(nameof(CanConfigure)); Changed(nameof(CanPlay)); Changed(nameof(IsPlaying));
        NotifyMediaSettings();
        foreach (var command in _commands) command.Raise();
    }
}
