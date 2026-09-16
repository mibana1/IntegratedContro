using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class HiperwallViewModel
{
    private bool _canOperate, _writeSupported, _lockAspect = true, _snapToGrid;
    private string _x = "", _y = "", _width = "", _height = "";
    private int _volume;
    private HiperwallItemRow? _targetZone;
    private string? _chosenZoneId;
    private Guid? _pendingEditId;
    private string? _selectAfterEditId;
    private string _editResult = "편집 결과가 여기에 표시됩니다.";
    public Func<string, bool> ConfirmCloseAll { get; set; } = _ => false;
    public string EditResult { get => _editResult; private set => Set(ref _editResult, value); }
    public string EditMode => _canOperate && _writeSupported ? "LIVE 편집" : "조회 모드";
    public bool CanOperate => Ready && _canOperate && _writeSupported && _view?.State == HiperwallConnectionState.Connected;
    public bool CanMove => CanOperate && SelectedInstance?.TryRectangle(out _, out _) == true && TargetZone?.Item.Id is not null;
    public bool CanAudio => CanOperate && SelectedInstance is { } item && HiperwallEditing.TryAudio(item.Item, out _, out _);
    public string EditHint => !_writeSupported ? "편집 기능을 지원하는 앱과 호스트로 함께 업데이트하세요." :
        !_canOperate ? "사용 시작과 전체 장비 제어 권한이 필요합니다." : _view?.State != HiperwallConnectionState.Connected ? "목록을 새로 고쳐 Controller 연결을 확인하세요." :
        SelectedContent is not null ? "Zone을 선택해 추가하세요. 원본 크기 미제공 시 너비·높이를 입력하세요." :
        SelectedInstance is { } selected && !selected.TryRectangle(out _, out _) ? selected.Geometry :
        SelectedInstance is not null && TargetZone?.Item.Id is null ? "이 인스턴스의 Zone 정보가 없습니다. 오른쪽에서 이동할 Zone을 선택하면 드래그·크기 조절이 활성화됩니다." :
        SelectedInstance is not null ? "마우스·손가락으로 이동, 오른쪽 아래 손잡이로 크기 조절. 놓으면 LIVE에 반영됩니다. 캔버스·열린 콘텐츠에서 Delete/Backspace 또는 선택 삭제로 제거하세요." : "Contents 또는 열린 인스턴스를 선택하세요.";
    public HiperwallItemRow? TargetZone
    {
        get => _targetZone;
        set { _chosenZoneId = value?.Item.Id; SetTargetZone(value); }
    }
    private void SetTargetZone(HiperwallItemRow? value)
    {
        if (!Set(ref _targetZone, value, nameof(TargetZone))) return;
        if (SelectedContent is not null && value?.TryRectangle(out var r, out _) == true)
        { EditX = Format(r.CenterX); EditY = Format(r.CenterY); }
        NotifyEditing();
    }
    public string EditX { get => _x; set { if (Set(ref _x, value)) NotifyEditing(); } }
    public string EditY { get => _y; set { if (Set(ref _y, value)) NotifyEditing(); } }
    public string EditWidth { get => _width; set { if (Set(ref _width, value)) NotifyEditing(); } }
    public string EditHeight { get => _height; set { if (Set(ref _height, value)) NotifyEditing(); } }
    public int EditVolume { get => _volume; set => Set(ref _volume, value); }
    public bool LockAspect { get => _lockAspect; set => Set(ref _lockAspect, value); }
    public bool SnapToGrid { get => _snapToGrid; set => Set(ref _snapToGrid, value); }
    public string MuteLabel => SelectedInstance is { } item && HiperwallEditing.TryAudio(item.Item, out _, out var muted) && muted ? "선택 음소거 해제" : "선택 음소거";
    public ObservableCollection<HiperwallEditReceipt> EditHistory { get; } = [];
    private HiperwallEditReceipt? _selectedEdit;
    public HiperwallEditReceipt? SelectedEdit { get => _selectedEdit; set { if (Set(ref _selectedEdit, value)) { Changed(nameof(EditDetails)); NotifyEditing(); } } }
    public string EditDetails => SelectedEdit is not { } r ? "전송 기록을 선택하면 요청자와 개별 결과를 확인할 수 있습니다." :
        $"{r.Requester.UserName} / {r.Requester.PcName}\n{r.AcceptedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{r.Endpoint}\n요청 ID: {r.Request.RequestId}\n" +
        string.Join("\n", r.Steps.Select(s => $"{s.Command.InstanceId ?? "Hiperwall 전체"} · {s.Message}"));
    public AsyncCommand AddContentCommand { get; private set; } = null!;
    public AsyncCommand ApplyGeometryCommand { get; private set; } = null!;
    public AsyncCommand CenterInZoneCommand { get; private set; } = null!;
    public AsyncCommand ApplyVolumeCommand { get; private set; } = null!;
    public AsyncCommand MuteCommand { get; private set; } = null!;
    public AsyncCommand MuteAllCommand { get; private set; } = null!;
    public AsyncCommand UnmuteAllCommand { get; private set; } = null!;
    public AsyncCommand CloseInstanceCommand { get; private set; } = null!;
    public AsyncCommand CloseAllCommand { get; private set; } = null!;
    public AsyncCommand CheckEditCommand { get; private set; } = null!;
    public AsyncCommand CancelEditCommand { get; private set; } = null!;
    private void InitializeEditing()
    {
        AddContentCommand = new(() => SendEdit(NewRequest(HiperwallEditAction.Open) with {
            Selector = SelectedContent!.Item.Id is null ? "name" : "uuid", ContentValue = SelectedContent.Item.Id ?? SelectedContent.Item.Name,
            ZoneId = TargetZone!.Item.Id, Layout = ReadLayout() }), () => CanOperate && SelectedContent is not null && TargetZone?.Item.Id is not null && TryLayout(out _));
        ApplyGeometryCommand = new(() => SendEdit(ChangeRequest() with { ZoneId = TargetZone!.Item.Id, Layout = ReadLayout() }), () => CanMove && TryLayout(out _));
        CenterInZoneCommand = new(async () =>
        {
            if (SelectedInstance is { } item && TargetZone is { } zone) await DropOnZone(item, zone);
        }, () => CanMove);
        ApplyVolumeCommand = new(() => SendEdit(ChangeRequest() with { Volume = EditVolume }), () => CanAudio);
        MuteCommand = new(() => { HiperwallEditing.TryAudio(SelectedInstance!.Item, out _, out var muted); return SendEdit(ChangeRequest() with { Muted = !muted }); }, () => CanAudio);
        MuteAllCommand = new(() => SendEdit(NewRequest(HiperwallEditAction.MuteAll) with { Muted = true, ExpectedRevision = InstanceSetRevision }), () => CanOperate);
        UnmuteAllCommand = new(() => SendEdit(NewRequest(HiperwallEditAction.MuteAll) with { Muted = false, ExpectedRevision = InstanceSetRevision }), () => CanOperate);
        CloseInstanceCommand = new(() => SendEdit(NewRequest(HiperwallEditAction.Close) with { InstanceId = SelectedInstance!.Item.Id,
            ExpectedRevision = HiperwallEditing.Revision([SelectedInstance.Item]) }), () => CanOperate && SelectedInstance?.Item.Id is not null);
        CloseAllCommand = new(async () =>
        {
            var request = NewRequest(HiperwallEditAction.CloseAll) with { ExpectedRevision = InstanceSetRevision };
            if (ConfirmCloseAll($"현재 조회한 열린 콘텐츠 {Instances.Count}개를 닫습니다. 다른 앱이 연 콘텐츠도 포함됩니다.\n목록이 변경되면 요청을 거부합니다. 계속하시겠습니까?")) await SendEdit(request);
        }, () => CanOperate && Instances.Count > 0);
        CheckEditCommand = new(CheckEdit, () => Ready && _writeSupported);
        CancelEditCommand = new(CancelPendingEdit, () => _client is not null && _session is not null && _canOperate && _writeSupported && SelectedEdit?.Active == true);
    }
    private async Task CancelPendingEdit()
    {
        var client = _client; var id = SelectedEdit?.Request.RequestId; var epoch = _epoch;
        if (client is null || id is null) return;
        try
        {
            var receipt = await client.Post<HiperwallEditReceipt>("/api/hiperwall/edits/cancel", new JobActionRequest(Generation, id.Value));
            if (epoch != _epoch) return;
            EditResult = receipt.Summary; await LoadEditHistory(client, default);
        }
        catch (Exception e) { if (epoch == _epoch) EditResult = e is ApiException api ? api.Message : "취소 결과 확인 필요 · 전송 기록을 확인하세요."; }
        finally { if (epoch == _epoch) NotifyEditing(); }
    }
    private string InstanceSetRevision => HiperwallEditing.Revision(Instances.Select(i => i.Item));
    private HiperwallEditRequest NewRequest(HiperwallEditAction action) => new(Guid.NewGuid(), Generation, _view!.ConfigurationVersion, action);
    private HiperwallEditRequest ChangeRequest() => NewRequest(HiperwallEditAction.Change) with { InstanceId = SelectedInstance!.Item.Id,
        ExpectedRevision = HiperwallEditing.Revision([SelectedInstance.Item]) };
    private bool TryLayout(out HiperwallLayout layout)
    {
        layout = new(0, 0, 0, 0);
        if (!HiperwallGeometry.TryNumber(EditX, out var x) || !HiperwallGeometry.TryNumber(EditY, out var y) ||
            !HiperwallGeometry.TryNumber(EditWidth, out var w) || !HiperwallGeometry.TryNumber(EditHeight, out var h)) return false;
        layout = new(x, y, w, h); return layout.IsValid;
    }
    private HiperwallLayout ReadLayout() => TryLayout(out var value) ? value : throw new ArgumentException("위치와 양수 크기를 입력하세요.");
    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private void SetLayout(HiperwallLayout r) { EditX = Format(r.X); EditY = Format(r.Y); EditWidth = Format(r.Width); EditHeight = Format(r.Height); }
    private void SelectForEditing()
    {
        if (SelectedInstance is { } instance)
        {
            if (instance.TryRectangle(out var r, out _)) SetLayout(HiperwallLayout.From(r));
            else { EditX = EditY = EditWidth = EditHeight = ""; }
            if (HiperwallEditing.TryAudio(instance.Item, out var volume, out _)) EditVolume = volume;
            // Some Controllers omit Object.zone for sources already on the wall.
            // Resolve their observed position before falling back to a manually chosen target.
            var zoneId = HiperwallGeometry.ResolveInstanceZone(instance.Item, Zones.Select(z => z.Item))?.Id;
            SetTargetZone(Zones.FirstOrDefault(z => z.Item.Id is not null && z.Item.Id == zoneId) ??
                Zones.FirstOrDefault(z => z.Item.Id is not null && z.Item.Id == _chosenZoneId));
        }
        else if (SelectedContent is { } content)
        {
            EditWidth = content.Item.Fields.GetValueOrDefault("width", ""); EditHeight = content.Item.Fields.GetValueOrDefault("height", "");
            if (TargetZone?.TryRectangle(out var zone, out _) == true) { EditX = Format(zone.CenterX); EditY = Format(zone.CenterY); }
            else { EditX = EditY = ""; }
        }
        NotifyEditing();
    }
    public async Task CommitGeometry(HiperwallItemRow item, HiperwallLayout layout)
    {
        if (!CanMove || !ReferenceEquals(item, SelectedInstance) || !layout.IsValid) return;
        SetLayout(layout); await SendEdit(ChangeRequest() with { ZoneId = TargetZone!.Item.Id, Layout = layout });
    }
    public bool CanDropOnZone(HiperwallItemRow item, HiperwallItemRow zone) => CanOperate &&
        Zones.Contains(zone) && zone.CanNavigateZone && (item.Kind == HiperwallRowKind.Instance
            ? Instances.Contains(item) && item.Item.Id is not null && item.TryRectangle(out _, out _)
            : item.Kind == HiperwallRowKind.Content && Contents.Contains(item));
    public async Task DropOnZone(HiperwallItemRow item, HiperwallItemRow zone)
    {
        if (!CanDropOnZone(item, zone) || !zone.TryRectangle(out var target, out _)) return;
        if (item.Kind == HiperwallRowKind.Content)
        {
            await DropContent(item, zone, target.CenterX, target.CenterY); return;
        }
        if (!item.TryRectangle(out var original, out _)) return;
        Selected = item; TargetZone = zone;
        // Use the observed pre-drag dimensions, never the inspector draft or the Zone's size.
        var layout = HiperwallLayout.From(original) with { X = target.CenterX, Y = target.CenterY };
        await SendEdit(ChangeRequest() with { ZoneId = zone.Item.Id, Layout = layout });
    }
    public async Task DropContent(HiperwallItemRow item, HiperwallItemRow zone, double x, double y)
    {
        if (!CanOperate || !Contents.Contains(item) || !Zones.Contains(zone)) return;
        Selected = item; TargetZone = zone; EditX = Format(x); EditY = Format(y);
        if (!TryLayout(out _)) { Message = "이 콘텐츠는 원본 크기를 제공하지 않습니다. 너비·높이를 입력하고 선택 콘텐츠 추가를 누르세요."; return; }
        await SendEdit(NewRequest(HiperwallEditAction.Open) with { Selector = item.Item.Id is null ? "name" : "uuid",
            ContentValue = item.Item.Id ?? item.Item.Name, ZoneId = zone.Item.Id, Layout = ReadLayout() });
    }
    private Task SendEdit(HiperwallEditRequest request) => Run(async (client, ct) =>
    {
        var epoch = _epoch; _pendingEditId = request.RequestId;
        try
        {
            var receipt = await client.Post<HiperwallEditReceipt>("/api/hiperwall/edit", request, ct, 35000);
            await LoadEditHistory(client, ct);
            while (receipt.Active && !ct.IsCancellationRequested)
            {
                if (epoch != _epoch) return null;
                EditResult = receipt.Summary; await Task.Delay(300, ct);
                receipt = await client.Get<HiperwallEditReceipt>($"/api/hiperwall/edits/{request.RequestId}", ct);
            }
            if (epoch != _epoch) return null;
            _pendingEditId = null; EditResult = receipt.Summary;
            if (request.Action == HiperwallEditAction.Open) _selectAfterEditId = receipt.Steps.FirstOrDefault()?.Command.InstanceId;
            await LoadEditHistory(client, ct);
            var view = await client.Post<Core.HiperwallView>("/api/hiperwall/refresh", cancellationToken: ct, timeoutMs: 35000);
            return view with { Message = receipt.Summary + " · 새 목록에서 표시·소리를 확인하세요." };
        }
        catch (ApiException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
        { if (epoch == _epoch) { _pendingEditId = null; EditResult = e.Message; } throw; }
        catch
        { if (epoch == _epoch) EditResult = $"접수·전송 결과 확인 필요 · 요청 {request.RequestId}. 전송 기록 확인을 누르세요. 자동 재전송하지 않습니다."; throw; }
    });
    private Task CheckEdit() => Run(async (client, ct) =>
    {
        if (_pendingEditId is { } id)
        {
            try { var receipt = await client.Get<HiperwallEditReceipt>($"/api/hiperwall/edits/{id}", ct); EditResult = receipt.Summary; if (!receipt.Active) _pendingEditId = null; }
            catch (ApiException e) when (e.Status == HttpStatusCode.NotFound) { EditResult = "접수 기록 없음 · 목록을 확인하고 필요한 동작을 다시 선택하세요."; _pendingEditId = null; }
        }
        await LoadEditHistory(client, ct);
        return await client.Post<Core.HiperwallView>("/api/hiperwall/refresh", cancellationToken: ct, timeoutMs: 35000);
    });
    private async Task LoadEditHistory(HostClient client, CancellationToken ct)
    {
        if (!_writeSupported) return;
        var epoch = _epoch;
        var receipts = await client.Get<HiperwallEditReceipt[]>("/api/hiperwall/edits", ct);
        if (epoch != _epoch || ct.IsCancellationRequested) return;
        var selected = SelectedEdit?.Request.RequestId;
        EditHistory.Clear(); foreach (var item in receipts) EditHistory.Add(item);
        SelectedEdit = EditHistory.FirstOrDefault(r => r.Request.RequestId == selected) ?? EditHistory.FirstOrDefault();
    }
    private void NotifyEditing()
    {
        foreach (var name in new[] { nameof(CanOperate), nameof(CanMove), nameof(CanAudio), nameof(EditHint), nameof(EditMode), nameof(MuteLabel) }) Changed(name);
        foreach (var command in new[] { AddContentCommand, ApplyGeometryCommand, CenterInZoneCommand, ApplyVolumeCommand, MuteCommand,
            MuteAllCommand, UnmuteAllCommand, CloseInstanceCommand, CloseAllCommand, CheckEditCommand, CancelEditCommand }) command?.Raise();
    }
}
