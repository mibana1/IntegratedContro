using System.Collections.ObjectModel;
using System.Net.Http;
using IntegratedContro.Core;
using HiperwallSettingsSnapshot = IntegratedContro.Core.HiperwallSettingsView;
using HiperwallSnapshot = IntegratedContro.Core.HiperwallView;

namespace IntegratedContro.App;

public enum HiperwallRowKind { Wall, Zone, Content, Instance }
public sealed record HiperwallItemRow(HiperwallItem Item, HiperwallRowKind Kind = HiperwallRowKind.Content)
{
    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? "(표시 이름 없음)" : Item.Name;
    public string Identity => Item.Id ?? (Kind == HiperwallRowKind.Content ? "UUID 없음 · 전체 이름으로 추가" : "식별자 없음 · 대상 확정 불가");
    public string KindName => Kind switch { HiperwallRowKind.Zone => "Zone", HiperwallRowKind.Instance => "열린 인스턴스", HiperwallRowKind.Wall => "Wall", _ => "Contents" };
    public string Type => Item.Fields.GetValueOrDefault(Kind == HiperwallRowKind.Instance ? "content.type" : "type", "유형 미제공");
    // Folder grouping is a view of the response name, never a synthetic inventory or identifier.
    public string Folder => Item.Name.Replace('\\', '/').LastIndexOf('/') is var split && split > 0 ?
        Item.Name.Replace('\\', '/')[..split] : "폴더 없는 콘텐츠";
    public bool TryRectangle(out HiperwallRectangle rect, out string reason)
    {
        rect = default; reason = "위치·크기 정보가 있는 Zone 또는 열린 인스턴스를 선택하세요.";
        return Kind == HiperwallRowKind.Zone ? HiperwallGeometry.TryZone(Item, out rect, out reason) :
            Kind == HiperwallRowKind.Instance && HiperwallGeometry.TryInstance(Item, out rect, out reason);
    }
    public string ZoneShortcutLabel => Name == Identity ? Name : $"{Name} · {Identity}";
    public bool CanNavigateZone => Kind == HiperwallRowKind.Zone && Item.Id is not null && TryRectangle(out _, out _);
    public string Geometry => TryRectangle(out var r, out var reason)
        ? FormattableString.Invariant($"중심 X  {r.CenterX:0.###}   /   Y  {r.CenterY:0.###}\n너비  {r.Width:0.###}   /   높이  {r.Height:0.###}\n절대 px · 화면 Y는 아래 방향")
        : reason;
    public string Details => $"{KindName}\n표시 이름: {Name}\n식별자: {Identity}\n출처: 마지막으로 받은 Controller 응답\n\n" +
        string.Join("\n", Item.Fields.Select(p => $"{p.Key}: {p.Value}"));
}
public sealed partial class HiperwallViewModel : Bindable
{
    private HostClient? _client;
    private Guid? _session;
    private bool _canConfigure, _admin, _supported, _busy;
    private int _hostVersion = -1, _editorVersion;
    private long _epoch;
    private CancellationTokenSource? _operation;
    private HiperwallSnapshot? _view;
    private string _message = "로그인 후 Hiperwall 조회를 사용할 수 있습니다.";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public HiperwallAuthentication Authentication { get; set; } = HiperwallAuthentication.Token;
    public string User { get; set; } = "";
    public string TimeoutText { get; set; } = "3000";
    public IEnumerable<HiperwallAuthentication> Authentications => Enum.GetValues<HiperwallAuthentication>();
    public Func<string> ReadSecret { get; set; } = () => "";
    public Action ClearSecret { get; set; } = () => { };
    public string SecretStatus { get; private set; } = "저장된 토큰 정보는 적용 설정을 불러온 뒤 확인하세요.";
    public string AppliedSettings { get; private set; } = "적용 설정 없음";
    public bool CanEdit => _canConfigure && !_busy;
    public bool IsBusy => _busy;
    public string CurrentConnection => _view is null ? "현재 연결 정보 없음" : _view.ConfigurationVersion == 0 ? "설정되지 않음" :
        $"{_view.ConnectionName} · {_view.Endpoint} · 설정 v{_view.ConfigurationVersion}";
    public string Status => _busy ? "연결 확인 중" : _view is null ? "조회 전" : HiperwallLabels.State(_view.State);
    public string ControllerInfo => _view?.Controller is not { } c ? "Controller 버전: 확인되지 않음" :
        $"Controller 응답 버전: {c.Version} · 인증: {c.Authentication} · 역할: {c.Role}";
    public string LastSuccess => $"마지막 연결 성공: {Time(_view?.LastSuccessAt)}";
    public ObservableCollection<HiperwallItemRow> Walls { get; } = [];
    public ObservableCollection<HiperwallItemRow> Zones { get; } = [];
    public ObservableCollection<HiperwallItemRow> Contents { get; } = [];
    public string WallState => ListStatus(_view?.Walls);
    public string ZoneState => ListStatus(_view?.Zones);
    public string ContentState => ListStatus(_view?.Contents);
    private HiperwallItemRow? _selected;
    public HiperwallItemRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var propertyName in new[] { nameof(Details), nameof(SelectedContent), nameof(SelectedZone),
                nameof(SelectedInstance), nameof(SelectedWall), nameof(SelectedName), nameof(SelectedGeometry) }) Changed(propertyName);
            SelectForEditing();
        }
    }
    public string Details => Selected?.Details ?? "콘텐츠 또는 열린 인스턴스를 선택하세요. UUID 없는 콘텐츠는 중복 없는 원문 전체 이름으로 추가합니다.";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand LoadSettingsCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand TestCommand { get; }

    public HiperwallViewModel()
    {
        InitializeWorkspace(); InitializeEditing(); InitializeLayouts();
        RefreshCommand = new(() => Run(async (client, ct) =>
        {
            InvalidateLists("최신 콘텐츠와 인스턴스를 조회합니다.");
            var view = await client.Post<HiperwallSnapshot>("/api/hiperwall/refresh", cancellationToken: ct, timeoutMs: 35000);
            await LoadEditHistory(client, ct); await LoadLayouts(client, ct); return view;
        }), () => Ready);
        TestCommand = new(() => Run(async (client, ct) =>
        {
            InvalidateLists("현재 저장·적용된 설정의 연결을 테스트합니다.");
            return await client.Post<HiperwallSnapshot>("/api/hiperwall/test", cancellationToken: ct, timeoutMs: 35000);
        }), () => Ready && _admin);
        LoadSettingsCommand = new(LoadSettings, () => Ready && _admin);
        SaveCommand = new(SaveSettings, () => Ready && _canConfigure);
    }
    public long PreviewEpoch => _epoch;
    public bool CanPreview => _client is not null && _session is not null && _view?.Contents.State == HiperwallListState.Available;
    public async Task<MediaPayload> ReadPreview(string selector, string value, CancellationToken ct)
    {
        if (!CanPreview) throw new InvalidOperationException("현재 Contents 목록이 필요합니다.");
        return await _client!.Post<MediaPayload>("/api/hiperwall/preview",
            new HiperwallPreviewRequest(ConfigurationVersion, selector, value), ct, 10000);
    }
    public int ConfigurationVersion => _view?.ConfigurationVersion ?? 0;
    private bool Ready => _client is not null && _session is not null && _supported && !_busy;
    public void UpdateContext(HostClient? client, Guid? session, bool admin, bool canConfigure, bool supported, int version, bool canOperate = false, bool writeSupported = false)
    {
        _canOperate = canOperate; _writeSupported = writeSupported;
        _admin = admin; _canConfigure = canConfigure; _supported = supported;
        var changed = _session != session || !ReferenceEquals(_client, client) || _hostVersion != version;
        if (changed)
        {
            var sessionChanged = _session != session || !ReferenceEquals(_client, client);
            Cancel();
            if (sessionChanged)
            {
                _pendingEditId = null; EditHistory.Clear(); SelectedEdit = null; ClearLayouts();
                _editorVersion = 0; Name = ""; Endpoint = ""; User = ""; TimeoutText = "3000";
                AppliedSettings = "적용 설정 조회 전"; SecretStatus = "적용 설정을 불러오세요.";
                foreach (var propertyName in new[] { nameof(Name), nameof(Endpoint), nameof(User), nameof(TimeoutText) }) Changed(propertyName);
            }
            _client = client; _session = session; _hostVersion = version; _view = null;
            InvalidateLists(session is null ? "로그인 후 조회할 수 있습니다." : "연결 설정이 변경되었거나 새 세션입니다. 새로 고침하세요.");
            ClearSecret();
            if (session is null) { AppliedSettings = "적용 설정 조회 전"; SecretStatus = "토큰 입력이 비워졌습니다."; }
            if (session is not null && supported) _ = LoadStatus();
        }
        if (!supported && session is not null) Message = "현재 호스트가 Hiperwall 조회를 지원하지 않습니다. 호스트 배포본을 확인하세요.";
        Notify();
    }
    private async Task LoadStatus() => await Run(async (client, ct) => { await LoadLayouts(client, ct); return await client.Get<HiperwallSnapshot>("/api/hiperwall/status", ct); });
    private async Task Run(Func<HostClient, CancellationToken, Task<HiperwallSnapshot?>> action)
    {
        if (_client is null || _busy) return;
        var epoch = _epoch;
        var cts = new CancellationTokenSource();
        _operation = cts; _busy = true; Notify();
        try
        {
            var value = await action(_client, cts.Token);
            if (epoch == _epoch && !cts.IsCancellationRequested && value is not null) Apply(value);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (epoch != _epoch) return;
            InvalidateLists("작업 실패: " + (e is ApiException api ? api.Message :
                e is HttpRequestException or OperationCanceledException ? "호스트 연결 또는 제한시간을 확인하세요." : "응답이나 설정을 확인하세요."));
        }
        finally
        {
            cts.Dispose();
            if (epoch == _epoch) { _operation = null; _busy = false; Notify(); }
        }
    }
    private async Task LoadSettings() => await Run(async (client, ct) =>
    {
        var epoch = _epoch;
        var s = await client.Get<HiperwallSettingsSnapshot>("/api/hiperwall/settings", ct);
        if (epoch != _epoch || ct.IsCancellationRequested) return null;
        SetSettings(s);
        return await client.Get<HiperwallSnapshot>("/api/hiperwall/status", ct);
    });
    private async Task SaveSettings() => await Run(async (client, ct) =>
    {
        if (!int.TryParse(TimeoutText, out var timeout)) { Message = "제한시간을 정수 ms로 입력하세요."; return null; }
        var epoch = _epoch;
        var secret = ReadSecret();
        try
        {
            var s = await client.Post<HiperwallSettingsSnapshot>("/api/hiperwall/settings",
                new SaveHiperwallRequest(Generation, _editorVersion, Name, Endpoint, Authentication, User, timeout,
                    string.IsNullOrEmpty(secret) ? null : secret), ct);
            if (epoch != _epoch || ct.IsCancellationRequested) return null;
            _hostVersion = s.Version;
            SetSettings(s);
            return await client.Get<HiperwallSnapshot>("/api/hiperwall/status", ct);
        }
        finally { ClearSecret(); secret = ""; }
    });
    public long Generation { get; set; }
    private void SetSettings(HiperwallSettingsSnapshot s)
    {
        _editorVersion = s.Version; Name = s.Name; Endpoint = s.Endpoint; Authentication = s.Authentication;
        User = s.User; TimeoutText = s.TimeoutMs.ToString();
        SecretStatus = s.HasSecret ? "토큰 저장됨 · 비워 두면 같은 대상·사용자의 토큰 유지" : "저장된 토큰 없음";
        AppliedSettings = s.Version == 0 ? "적용 설정 없음" :
            $"v{s.Version} · {s.Name}\n{s.Endpoint} · {s.Authentication} · 사용자 {s.User} · {s.TimeoutMs}ms";
        ClearSecret();
        foreach (var name in new[] { nameof(Name), nameof(Endpoint), nameof(Authentication), nameof(User), nameof(TimeoutText) }) Changed(name);
        Notify();
    }
    private void Apply(HiperwallSnapshot view)
    {
        if (view.ConfigurationVersion < _hostVersion) return;
        var selected = Selected; var zoneId = TargetZone?.Item.Id;
        _view = view;
        if (_editorVersion != view.ConfigurationVersion)
            AppliedSettings = $"현재 적용: v{view.ConfigurationVersion} · {view.ConnectionName}\n{view.Endpoint}\n편집 기준 v{_editorVersion} · 편집하려면 현재 적용 설정을 불러오세요.";
        Fill(Walls, view.Walls, HiperwallRowKind.Wall); Fill(Zones, view.Zones, HiperwallRowKind.Zone); Fill(Contents, view.Contents);
        Fill(Instances, view.Instances, HiperwallRowKind.Instance); UpdateWorkspace();
        Selected = selected is null ? null : Contents.Concat(Instances).Concat(Zones).Concat(Walls)
            .FirstOrDefault(r => r.Kind == selected.Kind && r.Item.Id == selected.Item.Id && r.Item.Name == selected.Item.Name);
        TargetZone = Zones.FirstOrDefault(z => z.Item.Id is not null && z.Item.Id == zoneId) ??
            Zones.FirstOrDefault(z => z.Item.Id is not null && z.Item.Id == TargetZone?.Item.Id);
        if (_selectAfterEditId is { } openedId) { Selected = Instances.FirstOrDefault(i => i.Item.Id == openedId); _selectAfterEditId = null; }
        Message = view.Message; Notify();
    }
    private void InvalidateLists(string message)
    {
        Walls.Clear(); Zones.Clear(); Contents.Clear(); Instances.Clear(); CanvasItems.Clear(); Selected = null;
        if (_view is not null) _view = _view with { State = HiperwallConnectionState.ConnectionFailed,
            Walls = HiperwallLabels.NotQueried(message) with { SucceededAt = _view.Walls.SucceededAt },
            Zones = HiperwallLabels.NotQueried(message) with { SucceededAt = _view.Zones.SucceededAt },
            Contents = HiperwallLabels.NotQueried(message) with { SucceededAt = _view.Contents.SucceededAt },
            Instances = HiperwallLabels.NotQueried(message) with { SucceededAt = _view.Instances.SucceededAt } };
        UpdateWorkspace();
        Message = message; Notify();
    }
    private static void Fill(ObservableCollection<HiperwallItemRow> rows, HiperwallList list, HiperwallRowKind kind = HiperwallRowKind.Content)
    {
        rows.Clear();
        if (list.State == HiperwallListState.Available)
            foreach (var item in list.Items) rows.Add(new(item, kind));
    }
    private static string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음";
    private static string ListStatus(HiperwallList? list) => list is null ? "조회 전" :
        $"{(list.State switch { HiperwallListState.Available => list.Items.Length == 0 ? "조회 성공 · 빈 목록" : $"조회 성공 · {list.Items.Length}개",
            HiperwallListState.Unsupported => "미지원", HiperwallListState.Failed => "조회 실패", _ => "조회 필요" })}\n{list.Reason}\n마지막 목록 조회 성공: {Time(list.SucceededAt)}";
    public void Cancel()
    {
        _epoch++; _operation?.Cancel(); _operation = null; _busy = false; ClearSecret();
    }
    public void Close() { Cancel(); ClearLayouts(); _client = null; _session = null; _view = null; InvalidateLists("조회가 종료되었습니다."); }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanEdit), nameof(Status), nameof(CurrentConnection), nameof(ControllerInfo),
            nameof(LastSuccess), nameof(WallState), nameof(ZoneState), nameof(ContentState), nameof(InstanceState), nameof(InstanceBrief), nameof(ZoneBrief), nameof(WallBrief), nameof(ContentCount), nameof(CanvasSummary), nameof(AppliedSettings), nameof(SecretStatus) }) Changed(name);
        NotifyEditing(); NotifyLayouts();
        RefreshCommand?.Raise(); LoadSettingsCommand?.Raise(); SaveCommand?.Raise(); TestCommand?.Raise();
    }
}
