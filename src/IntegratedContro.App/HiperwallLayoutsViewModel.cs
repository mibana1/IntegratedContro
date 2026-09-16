using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class HiperwallViewModel
{
    public ObservableCollection<SavedHiperwallLayout> SavedLayouts { get; } = [];
    public ObservableCollection<HiperwallPlacement> DraftPlacements { get; } = [];
    public ObservableCollection<HiperwallDisplayJob> DisplayJobs { get; } = [];
    private SavedHiperwallLayout? _selectedLayout;
    private HiperwallPlacement? _selectedPlacement;
    private HiperwallDisplayJob? _selectedDisplay;
    private Guid _draftId = Guid.NewGuid();
    private int _draftVersion;
    private string _layoutName = "", _layoutMessage = "배치를 저장해도 LIVE는 변경되지 않습니다.";
    private HiperwallItemRow? _draftContent, _draftZone;
    private string _draftX = "0", _draftY = "0", _draftWidth = "640", _draftHeight = "360", _durationSeconds = "15";
    private DisplayDurationMode _durationMode;
    public SavedHiperwallLayout? SelectedLayout { get => _selectedLayout; set { Set(ref _selectedLayout, value); NotifyLayouts(); } }
    public HiperwallDisplayJob? SelectedDisplay { get => _selectedDisplay; set { Set(ref _selectedDisplay, value); Changed(nameof(DisplayDetails)); NotifyLayouts(); } }
    public HiperwallPlacement? SelectedPlacement
    {
        get => _selectedPlacement;
        set
        {
            if (!Set(ref _selectedPlacement, value) || value is null) { NotifyLayouts(); return; }
            DraftContent = Contents.SingleOrDefault(c => value.Selector == "uuid" ? c.Item.Id == value.ContentValue : c.Item.Name == value.ContentValue && c.Item.Id is null);
            DraftZone = Zones.SingleOrDefault(z => z.Item.Id == value.ZoneId);
            DraftX = Format(value.Layout.X); DraftY = Format(value.Layout.Y); DraftWidth = Format(value.Layout.Width); DraftHeight = Format(value.Layout.Height);
            NotifyLayouts();
        }
    }
    public string LayoutName { get => _layoutName; set { Set(ref _layoutName, value); NotifyLayouts(); } }
    public string LayoutMessage { get => _layoutMessage; private set => Set(ref _layoutMessage, value); }
    public string DraftVersionLabel => _draftVersion == 0 ? "새 배치 초안" : $"저장 v{_draftVersion}에서 편집 중";
    public HiperwallItemRow? DraftContent
    {
        get => _draftContent;
        set
        {
            if (Set(ref _draftContent, value) && value is not null)
            { DraftWidth = value.Item.Fields.GetValueOrDefault("width", "640"); DraftHeight = value.Item.Fields.GetValueOrDefault("height", "360"); }
            NotifyLayouts();
        }
    }
    public HiperwallItemRow? DraftZone
    {
        get => _draftZone;
        set
        {
            if (Set(ref _draftZone, value) && value?.TryRectangle(out var rect, out _) == true)
            { DraftX = Format(rect.CenterX); DraftY = Format(rect.CenterY); }
            NotifyLayouts();
        }
    }
    public string DraftX { get => _draftX; set { Set(ref _draftX, value); NotifyLayouts(); } }
    public string DraftY { get => _draftY; set { Set(ref _draftY, value); NotifyLayouts(); } }
    public string DraftWidth { get => _draftWidth; set { Set(ref _draftWidth, value); NotifyLayouts(); } }
    public string DraftHeight { get => _draftHeight; set { Set(ref _draftHeight, value); NotifyLayouts(); } }
    public DisplayDurationMode DurationMode { get => _durationMode; set { Set(ref _durationMode, value); Changed(nameof(IsTimedDuration)); NotifyLayouts(); } }
    public bool IsTimedDuration => DurationMode == DisplayDurationMode.Timed;
    public string DurationSeconds { get => _durationSeconds; set { Set(ref _durationSeconds, value); NotifyLayouts(); } }
    public string DisplayDetails => SelectedDisplay is not { } j ? "표시 작업을 선택하세요. 사용 종료·UI 종료 후에도 호스트가 표시와 정리를 이어갑니다." :
        $"{j.Summary}\n{j.Schedule}\n원 요청자: {j.Requester.UserName} / {j.Requester.PcName}\n세션: {j.Requester.Id}\n접수: {j.AcceptedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nController: {j.Endpoint}\n요청: {j.Request.RequestId}\n종료 요청자: {j.StopperName ?? "-"}\n\n" +
        string.Join("\n", j.Targets.Select(t => $"{t.Command.ContentValue} · {t.Command.ZoneId}\n{t.Command.InstanceId}\n{t.Message}"));
    public AsyncCommand NewLayoutCommand { get; private set; } = null!;
    public AsyncCommand LoadLayoutCommand { get; private set; } = null!;
    public AsyncCommand SaveLayoutCommand { get; private set; } = null!;
    public AsyncCommand DeleteLayoutCommand { get; private set; } = null!;
    public AsyncCommand CaptureLayoutCommand { get; private set; } = null!;
    public AsyncCommand AddPlacementCommand { get; private set; } = null!;
    public AsyncCommand UpdatePlacementCommand { get; private set; } = null!;
    public AsyncCommand RemovePlacementCommand { get; private set; } = null!;
    public AsyncCommand ShowLayoutCommand { get; private set; } = null!;
    public AsyncCommand TestLayoutCommand { get; private set; } = null!;
    public AsyncCommand RefreshLayoutsCommand { get; private set; } = null!;
    public AsyncCommand StopDisplayCommand { get; private set; } = null!;
    private bool _layoutsSupported;
    public void UpdateDisplayJobs(HiperwallDisplayJob[] jobs, bool supported)
    {
        _layoutsSupported = supported;
        if (_session is null) return;
        if (!supported) LayoutMessage = "저장 배치 기능을 사용하려면 App과 ControlHost를 같은 최신 배포본으로 업데이트하세요.";
        var id = SelectedDisplay?.Request.RequestId;
        DisplayJobs.Clear(); foreach (var job in jobs) DisplayJobs.Add(job);
        SelectedDisplay = DisplayJobs.FirstOrDefault(j => j.Request.RequestId == id) ?? DisplayJobs.FirstOrDefault();
    }
    private bool CanSaveLayout => _layoutsSupported && Ready && _canOperate && _writeSupported && _hostVersion > 0;
    private void InitializeLayouts()
    {
        NewLayoutCommand = new(() => { ResetDraft(); return Task.CompletedTask; }, () => Ready);
        LoadLayoutCommand = new(() =>
        {
            var l = SelectedLayout!; _draftId = l.Id; _draftVersion = l.Version; LayoutName = l.Name;
            DurationMode = l.Duration.Mode; DurationSeconds = (l.Duration.Seconds ?? 15).ToString();
            DraftPlacements.Clear(); foreach (var p in l.Placements) DraftPlacements.Add(p);
            SelectedPlacement = null; LayoutMessage = "저장 배치를 초안으로 불러왔습니다. 수정 후 저장해도 기존 표시 작업은 유지됩니다.";
            Changed(nameof(DraftVersionLabel)); NotifyLayouts(); return Task.CompletedTask;
        }, () => Ready && SelectedLayout is not null);
        AddPlacementCommand = new(() => { DraftPlacements.Add(ReadPlacement()); NotifyLayouts(); return Task.CompletedTask; },
            () => Ready && DraftPlacements.Count < 100 && TryPlacement(out _));
        UpdatePlacementCommand = new(() => {
            var index = DraftPlacements.IndexOf(SelectedPlacement!); var p = ReadPlacement() with { Volume = SelectedPlacement!.Volume, Muted = SelectedPlacement.Muted };
            DraftPlacements[index] = p; SelectedPlacement = p; return Task.CompletedTask;
        }, () => Ready && SelectedPlacement is not null && TryPlacement(out _));
        RemovePlacementCommand = new(() => { DraftPlacements.Remove(SelectedPlacement!); SelectedPlacement = null; NotifyLayouts(); return Task.CompletedTask; },
            () => Ready && SelectedPlacement is not null);
        CaptureLayoutCommand = new(() =>
        {
            var captured = new List<HiperwallPlacement>();
            var zones = Zones.Select(row => row.Item).ToArray();
            foreach (var row in Instances)
            {
                var item = row.Item; var uuid = item.Fields.GetValueOrDefault("content.uuid"); var zone = HiperwallGeometry.ResolveInstanceZone(item, zones)?.Id;
                if (!row.TryRectangle(out var rect, out _) || string.IsNullOrEmpty(zone))
                { LayoutMessage = $"위치·크기·Zone을 확인할 수 없는 콘텐츠: {item.Name} (인스턴스 ID: {item.Id ?? "없음"}). LIVE에서 확인하거나 초안에 직접 추가하세요."; return Task.CompletedTask; }
                var audio = HiperwallEditing.TryAudio(item, out var volume, out var muted);
                captured.Add(new(uuid is null ? "name" : "uuid", uuid ?? item.Name, zone, HiperwallLayout.From(rect), audio ? volume : null, audio ? muted : null));
            }
            DraftPlacements.Clear(); foreach (var p in captured) DraftPlacements.Add(p);
            SelectedPlacement = null; LayoutMessage = "현재 조회한 LIVE를 초안으로 복사했습니다. 저장하거나 15초 테스트할 수 있습니다.";
            NotifyLayouts(); return Task.CompletedTask;
        }, () => Ready && Instances.Count is > 0 and <= 100);
        SaveLayoutCommand = new(() => LayoutRun(async (client, ct, epoch) =>
        {
            var result = await client.Post<SavedHiperwallLayout>("/api/hiperwall/layouts/save",
                new SaveHiperwallLayoutRequest(Generation, _draftId, _draftVersion, _hostVersion, LayoutName, DraftPlacements.ToArray(), ReadDuration()), ct);
            if (epoch != _epoch) return;
            _draftVersion = result.Version; Changed(nameof(DraftVersionLabel));
            await LoadLayouts(client, ct); SelectedLayout = SavedLayouts.Single(l => l.Id == result.Id);
            LayoutMessage = $"배치 저장 완료 · v{result.Version} · LIVE 변경 없음";
        }), () => CanSaveLayout && !string.IsNullOrWhiteSpace(LayoutName) && DraftPlacements.Count > 0 && ReadDuration().IsValid);
        DeleteLayoutCommand = new(() => LayoutRun(async (client, ct, epoch) =>
        {
            var l = SelectedLayout!;
            await client.Post<bool>("/api/hiperwall/layouts/delete", new DeleteHiperwallLayoutRequest(Generation, l.Id, l.Version), ct);
            if (epoch != _epoch) return;
            if (_draftId == l.Id) { _draftId = Guid.NewGuid(); _draftVersion = 0; Changed(nameof(DraftVersionLabel)); }
            await LoadLayouts(client, ct); LayoutMessage = "배치 삭제 완료 · 접수된 표시는 유지됩니다.";
        }), () => CanSaveLayout && SelectedLayout is not null);
        ShowLayoutCommand = new(() => SubmitDisplay(new(Guid.NewGuid(), Generation, _hostVersion, SelectedLayout!.Id, SelectedLayout.Version)),
            () => _layoutsSupported && CanOperate && SelectedLayout?.ConfigurationVersion == _hostVersion);
        TestLayoutCommand = new(() => SubmitDisplay(new(Guid.NewGuid(), Generation, _hostVersion, TestPlacements: DraftPlacements.ToArray())),
            () => _layoutsSupported && CanOperate && DraftPlacements.Count > 0);
        RefreshLayoutsCommand = new(() => LayoutRun(async (client, ct, epoch) => { await LoadLayouts(client, ct); if (epoch == _epoch) LayoutMessage = "저장 배치와 표시·정리 상태를 갱신했습니다."; }), () => Ready && _layoutsSupported);
        StopDisplayCommand = new(() => LayoutRun(async (client, ct, epoch) =>
        {
            await client.Post<HiperwallDisplayJob>("/api/hiperwall/displays/stop", new JobActionRequest(Generation, SelectedDisplay!.Request.RequestId), ct);
            if (epoch != _epoch) return;
            await LoadLayouts(client, ct); LayoutMessage = "미전송 표시 중단과 열린 인스턴스 정리를 요청했습니다. 결과를 확인하세요.";
        }), () => CanSaveLayout && SelectedDisplay?.Outstanding == true);
    }
    private void ResetDraft()
    {
        _draftId = Guid.NewGuid(); _draftVersion = 0; LayoutName = ""; DurationMode = DisplayDurationMode.Default;
        DraftPlacements.Clear(); SelectedPlacement = null; Changed(nameof(DraftVersionLabel)); NotifyLayouts();
    }
    private DisplayDuration ReadDuration() => new(DurationMode,
        DurationMode == DisplayDurationMode.Timed ? int.TryParse(DurationSeconds, out var n) ? n : 0 : null);
    private bool TryPlacement(out HiperwallPlacement placement)
    {
        placement = null!;
        if (DraftContent is null || DraftZone?.Item.Id is null || !HiperwallGeometry.TryNumber(DraftX, out var x) ||
            !HiperwallGeometry.TryNumber(DraftY, out var y) || !HiperwallGeometry.TryNumber(DraftWidth, out var w) || !HiperwallGeometry.TryNumber(DraftHeight, out var h)) return false;
        var layout = new HiperwallLayout(x, y, w, h);
        placement = new(DraftContent.Item.Id is null ? "name" : "uuid", DraftContent.Item.Id ?? DraftContent.Item.Name, DraftZone.Item.Id, layout);
        return layout.IsValid;
    }
    private HiperwallPlacement ReadPlacement() => TryPlacement(out var p) ? p : throw new InvalidOperationException("배치 입력을 확인하세요.");
    private Task SubmitDisplay(HiperwallDisplayRequest request) => LayoutRun(async (client, ct, epoch) =>
    {
        LayoutMessage = $"표시 접수 확인 중 · 요청 {request.RequestId}";
        var job = await client.Post<HiperwallDisplayJob>("/api/hiperwall/displays/show", request, ct, 35000);
        if (epoch != _epoch) return;
        await LoadLayouts(client, ct); SelectedDisplay = DisplayJobs.FirstOrDefault(j => j.Request.RequestId == job.Request.RequestId);
        LayoutMessage = $"표시 접수 완료 · {job.Schedule} · 저장 배치와 작업 상태 새로 고침으로 결과를 확인하세요.";
    });
    private Task LayoutRun(Func<HostClient, CancellationToken, long, Task> action) => Run(async (client, ct) =>
    {
        var epoch = _epoch;
        try { await action(client, ct, epoch); }
        catch (Exception e) { if (epoch == _epoch) LayoutMessage = e is ApiException api ? api.Message : "결과 확인이 필요합니다. 저장 배치·작업 새로 고침에서 확인한 뒤 다시 조작하세요."; }
        return null;
    });
    private async Task LoadLayouts(HostClient client, CancellationToken ct)
    {
        if (!_writeSupported || !_layoutsSupported) return;
        var epoch = _epoch; var result = await client.Get<HiperwallDisplayView>("/api/hiperwall/displays", ct);
        if (epoch != _epoch || ct.IsCancellationRequested) return;
        var layoutId = SelectedLayout?.Id; var jobId = SelectedDisplay?.Request.RequestId;
        SavedLayouts.Clear(); foreach (var l in result.Layouts) SavedLayouts.Add(l);
        DisplayJobs.Clear(); foreach (var j in result.Jobs) DisplayJobs.Add(j);
        SelectedLayout = SavedLayouts.FirstOrDefault(l => l.Id == layoutId);
        SelectedDisplay = DisplayJobs.FirstOrDefault(j => j.Request.RequestId == jobId) ?? DisplayJobs.FirstOrDefault();
        NotifyLayouts();
    }
    private void ClearLayouts()
    {
        SavedLayouts.Clear(); DisplayJobs.Clear(); SelectedLayout = null; SelectedDisplay = null; ResetDraft();
        DraftContent = DraftZone = null;
        foreach (var row in LayoutSlots) row.Update(null);
        _slotsSupported = false; SlotMessage = "";
    }
    private void NotifyLayouts()
    {
        foreach (var c in new[] { NewLayoutCommand, LoadLayoutCommand, SaveLayoutCommand, DeleteLayoutCommand, CaptureLayoutCommand,
            AddPlacementCommand, UpdatePlacementCommand, RemovePlacementCommand, ShowLayoutCommand, TestLayoutCommand, RefreshLayoutsCommand, StopDisplayCommand }) c?.Raise();
    }
}
