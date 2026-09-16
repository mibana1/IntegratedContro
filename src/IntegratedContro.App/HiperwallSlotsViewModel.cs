using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class HiperwallSlotRow(int number, Action select, Func<bool> canSelect) : Bindable
{
    public int Number { get; } = number;
    public string Title => $"슬롯 {Number}";
    public HiperwallSlot? Snapshot { get; private set; }
    public int Version => Snapshot?.Version ?? 0;
    public bool HasSaved => Snapshot is { IsEmpty: false };
    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string Detail => HasSaved ? $"콘텐츠 {Snapshot!.Placements.Length}개 · v{Version}" : "비어 있음";
    public string Description => HasSaved
        ? $"{Title} · {Snapshot!.Placements.Length}개 · 저장 {Snapshot.SavedAt?.ToLocalTime():MM-dd HH:mm}\n" +
            string.Join("\n", Snapshot.Placements.Select(p => p.Label))
        : $"{Title} · 현재 Zone의 콘텐츠 배치를 저장할 수 있습니다.";
    public AsyncCommand SelectCommand { get; } = new(() => { select(); return Task.CompletedTask; }, canSelect);
    public void Update(HiperwallSlot? slot)
    {
        Snapshot = slot;
        Changed(nameof(Version)); Changed(nameof(HasSaved)); Changed(nameof(Detail)); Changed(nameof(Description));
    }
}
public sealed partial class HiperwallViewModel
{
    public ObservableCollection<HiperwallSlotRow> LayoutSlots { get; } = [];
    private HiperwallSlotRow? _selectedSlot;
    private bool _slotsSupported;
    private string _slotMessage = "";
    public HiperwallSlotRow? SelectedSlot
    {
        get => _selectedSlot;
        private set
        {
            if (!Set(ref _selectedSlot, value)) return;
            foreach (var row in LayoutSlots) row.IsSelected = ReferenceEquals(row, value);
            NotifySlots();
        }
    }
    public string SlotMessage { get => _slotMessage; private set => Set(ref _slotMessage, value); }
    private string? SlotAccessIssue => _client is null || _session is null ? "호스트에 로그인한 뒤 사용하세요." :
        _busy ? "현재 작업이 끝나면 사용할 수 있습니다." :
        !_slotsSupported ? "연결된 ControlHost가 저장 슬롯을 지원하지 않습니다. 슬롯 기능이 포함된 ControlHost로 전환하세요." :
        !_canOperate ? "사용 시작과 전체 장비 제어 권한이 필요합니다." :
        !_supported || !_writeSupported ? "현재 호스트에서 Hiperwall 편집을 사용할 수 없습니다." :
        _hostVersion <= 0 ? "먼저 Hiperwall 연결을 설정하세요." : null;
    private string? SaveSlotIssue => SlotAccessIssue ??
        (_view?.State != HiperwallConnectionState.Connected ? "목록을 새로 고쳐 Controller 연결을 확인하세요." :
        SelectedSlot is null ? "저장할 슬롯을 선택하세요." :
        Instances.Count == 0 ? "Zone에 콘텐츠를 추가한 뒤 저장하세요." :
        Instances.Count > 100 ? "한 슬롯에는 콘텐츠를 100개까지 저장할 수 있습니다." : null);
    private string? DeleteSlotIssue => SlotAccessIssue ??
        (SelectedSlot?.HasSaved != true ? "저장된 슬롯을 선택하세요." : null);
    private string? RestoreSlotIssue => SlotAccessIssue ??
        (_view?.State != HiperwallConnectionState.Connected ? "목록을 새로 고쳐 Controller 연결을 확인하세요." :
        SelectedSlot?.HasSaved != true ? "불러올 저장 슬롯을 선택하세요." :
        SelectedSlot.Snapshot!.ConfigurationVersion != _hostVersion ? "Controller 설정이 변경되었습니다. 현재 연결에서 다시 저장하세요." : null);
    public string SaveSlotToolTip => SaveSlotIssue ?? "현재 Zone의 콘텐츠·위치·크기를 선택 슬롯에 저장합니다. 기존 저장 정보는 덮어씁니다.";
    public string DeleteSlotToolTip => DeleteSlotIssue ?? "선택 슬롯의 저장 정보만 삭제합니다. 현재 화면은 유지됩니다.";
    public string RestoreSlotToolTip => RestoreSlotIssue ?? "현재 열린 콘텐츠 전체를 선택 슬롯의 저장 상태로 교체합니다.";
    public AsyncCommand SaveSlotCommand { get; private set; } = null!;
    public AsyncCommand DeleteSlotCommand { get; private set; } = null!;
    public AsyncCommand RestoreSlotCommand { get; private set; } = null!;
    public void UpdateSlots(HiperwallSlot[] slots, bool supported)
    {
        _slotsSupported = supported;
        foreach (var row in LayoutSlots)
        {
            var slot = slots.SingleOrDefault(s => s.Number == row.Number);
            // A polling response captured before our save/delete must not roll back the local result.
            if ((slot?.Version ?? 0) >= row.Version) row.Update(slot);
        }
        NotifySlots();
    }
    private void InitializeSlots()
    {
        for (var number = 1; number <= HiperwallSlot.Count; number++)
        {
            var n = number;
            LayoutSlots.Add(new(n, () => SelectedSlot = LayoutSlots[n - 1], () => !_busy));
        }
        SelectedSlot = LayoutSlots[0];
        SaveSlotCommand = new(() => SlotRun(async (client, ct, epoch) =>
        {
            var row = SelectedSlot!;
            var saved = await client.Post<HiperwallSlot>("/api/hiperwall/slots/save",
                new SaveHiperwallSlotRequest(Generation, row.Number, row.Version, _hostVersion, InstanceSetRevision), ct, 35000);
            if (epoch != _epoch) return;
            row.Update(saved); SlotMessage = $"슬롯 {row.Number} 저장 완료 · 콘텐츠 {saved.Placements.Length}개 · 현재 화면 유지";
        }), () => SaveSlotIssue is null);
        DeleteSlotCommand = new(() => SlotRun(async (client, ct, epoch) =>
        {
            var row = SelectedSlot!; var version = row.Version;
            await client.Post<bool>("/api/hiperwall/slots/delete", new DeleteHiperwallSlotRequest(Generation, row.Number, version), ct);
            if (epoch != _epoch) return;
            row.Update(new(row.Number, version + 1, 0, [], null));
            SlotMessage = $"슬롯 {row.Number} 저장 정보 삭제 완료 · 현재 화면 유지";
        }), () => DeleteSlotIssue is null);
        RestoreSlotCommand = new(async () =>
        {
            var row = SelectedSlot!;
            var request = NewRequest(HiperwallEditAction.RestoreSlot) with {
                SlotNumber = row.Number, SlotVersion = row.Version, ExpectedRevision = InstanceSetRevision
            };
            SlotMessage = $"슬롯 {row.Number} 불러오는 중 · 현재 콘텐츠를 저장 상태로 교체합니다.";
            await SendEdit(request);
            SlotMessage = $"슬롯 {row.Number} · {EditResult}";
        }, () => RestoreSlotIssue is null);
    }
    private Task SlotRun(Func<HostClient, CancellationToken, long, Task> action) => Run(async (client, ct) =>
    {
        var epoch = _epoch;
        try { await action(client, ct, epoch); }
        catch (Exception e)
        {
            if (epoch == _epoch) SlotMessage = e is ApiException api ? api.Message : "슬롯 처리 결과 확인이 필요합니다. 새로 고침 후 확인하세요.";
        }
        if (epoch == _epoch) NotifySlots();
        return null;
    });
    private void NotifySlots()
    {
        Changed(nameof(SaveSlotToolTip)); Changed(nameof(DeleteSlotToolTip)); Changed(nameof(RestoreSlotToolTip));
        foreach (var row in LayoutSlots) row.SelectCommand.Raise();
        SaveSlotCommand?.Raise(); DeleteSlotCommand?.Raise(); RestoreSlotCommand?.Raise();
    }
}
