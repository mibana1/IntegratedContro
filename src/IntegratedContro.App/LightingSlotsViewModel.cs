using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class LightSlotRow(int number) : Bindable
{
    public int Number { get; } = number;
    public LightSlot? Snapshot { get; private set; }
    public int Version => Snapshot?.Version ?? 0;
    public bool HasSaved => Snapshot is { IsEmpty: false };
    public string Title => $"슬롯 {Number} · {(HasSaved ? Snapshot!.Name : "비어 있음")}";
    public string Summary => HasSaved
        ? $"그룹 {Snapshot!.Layout.Groups.Length} · ON {Snapshot.PowerStates.Count(s => s.Value == 1)} / OFF {Snapshot.PowerStates.Count(s => s.Value == 0)}"
        : "현재 그룹·순서·전원을 저장";
    public string Detail => HasSaved ? $"{Title}\n{Summary}\n{Snapshot!.SavedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : Summary;
    private bool _selected;
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }
    public AsyncCommand SelectCommand { get; internal set; } = null!;
    internal void Update(LightSlot? value)
    {
        Snapshot = value;
        foreach (var name in new[] { nameof(Title), nameof(Summary), nameof(Detail), nameof(HasSaved), nameof(Version) }) Changed(name);
    }
}

public sealed partial class LightingViewModel
{
    public ObservableCollection<LightSlotRow> LightSlots { get; } = [];
    private LightSlotRow? _selectedLightSlot;
    public LightSlotRow? SelectedLightSlot
    {
        get => _selectedLightSlot;
        set
        {
            if (!Set(ref _selectedLightSlot, value)) return;
            foreach (var row in LightSlots) row.IsSelected = row == value;
            LoadLightSlotName(); NotifyLightSlots();
        }
    }
    private string _lightSlotName = "";
    private bool _lightSlotNameEdited;
    public string LightSlotName
    {
        get => _lightSlotName;
        set { if (Set(ref _lightSlotName, value)) { _lightSlotNameEdited = true; NotifyLightSlots(); } }
    }
    public AsyncCommand SaveLightSlotCommand { get; private set; } = null!;
    public AsyncCommand RestoreLightSlotCommand { get; private set; } = null!;
    public AsyncCommand DeleteLightSlotCommand { get; private set; } = null!;
    public AsyncCommand ReadSlotStatesCommand { get; private set; } = null!;
    private string? LightSlotAccessIssue => !Context.Connected ? "호스트에 로그인한 뒤 사용하세요." :
        State?.LightSlotsSupported != true ? "장비 슬롯을 사용하려면 최신 ControlHost가 필요합니다." :
        !CanConfigure ? "슬롯 저장·불러오기에는 관리자 사용권이 필요합니다." :
        HasPending ? "이전 요청의 접수 여부를 먼저 확인하세요." :
        EditingLightOrder ? "그룹·순서 편집을 저장하거나 취소한 뒤 슬롯을 사용하세요." :
        SelectedLightSlot is null ? "저장 슬롯을 선택하세요." : null;
    private string? SaveLightSlotIssue => LightSlotAccessIssue ??
        (Lights.Count is < 1 or > 1000 ? "슬롯에 저장할 장비 1~1000개가 필요합니다." :
        string.IsNullOrWhiteSpace(LightSlotName) || LightSlotName.Length > 50 ? "슬롯 이름을 1~50자로 입력하세요." :
        Lights.Select(c => (Card: c, Reason: LightBlockReason(c.Id))).FirstOrDefault(x => x.Reason is not null) is { Reason: { } reason } blocked
            ? $"{blocked.Card.Name}: {reason}" : null);
    public string LightSlotHint => LightSlotAccessIssue ??
        "저장: 현재 그룹·순서·ON/OFF 기록 · 불러오기: 배치 복원 후 저장된 전원을 순서대로 적용";
    public string SaveLightSlotHint => SaveLightSlotIssue ?? "선택 슬롯에 현재 상태를 저장합니다. 기존 저장 정보는 덮어씁니다.";
    public string RestoreLightSlotHint => LightSlotAccessIssue ?? (SelectedLightSlot?.HasSaved != true ? "저장된 슬롯을 선택하세요." :
        "그룹·순서를 복원하고 장비 전원을 적용합니다. 실행 결과는 작업·교대에서 확인하세요.");

    private void InitializeLightSlots()
    {
        for (var number = 1; number <= LightSlot.Count; number++)
        {
            var row = new LightSlotRow(number);
            row.SelectCommand = LocalCommand(() => SelectedLightSlot = row, () => true);
            LightSlots.Add(row);
        }
        SelectedLightSlot = LightSlots[0];
        SaveLightSlotCommand = Command(async () =>
        {
            var row = SelectedLightSlot!; var name = LightSlotName; var session = State!.Session.Id;
            var targets = Lights.Select(c =>
            {
                var role = LightRole(c.Id)!; var power = LightPower(c.Id)!;
                return new LightPowerTarget(role.Id, new(c.Id, c.Device.PcId, c.Device.Version, role.Version, power.Value, power.At));
            }).ToArray();
            var saved = await _host.SaveLightSlotAsync(new(Generation, row.Number, row.Version, name, State.LightLayout.Version, targets));
            if (State?.Session.Id != session || Context.Closing) return;
            row.Update(saved);
            if (SelectedLightSlot == row && LightSlotName == name) LoadLightSlotName();
            ReportStatus($"슬롯 {row.Number} 저장 완료 · 그룹·순서·ON/OFF를 저장했습니다.");
        }, () => SaveLightSlotIssue is null);
        RestoreLightSlotCommand = Command(() => _host.RestoreLightSlotAsync(
            new(Guid.NewGuid(), Generation, SelectedLightSlot!.Number, SelectedLightSlot.Version, State!.LightLayout.Version)),
            () => LightSlotAccessIssue is null && SelectedLightSlot?.HasSaved == true);
        DeleteLightSlotCommand = Command(async () =>
        {
            var row = SelectedLightSlot!; var session = State!.Session.Id;
            var cleared = await _host.DeleteLightSlotAsync(new(Generation, row.Number, row.Version));
            if (State?.Session.Id != session || Context.Closing) return;
            row.Update(cleared);
            if (SelectedLightSlot == row) LoadLightSlotName();
            ReportStatus($"슬롯 {row.Number} 저장 정보를 삭제했습니다. 현재 배치와 전원은 유지됩니다.");
        }, () => LightSlotAccessIssue is null && SelectedLightSlot?.HasSaved == true);
        ReadSlotStatesCommand = Command(async () =>
        {
            var session = State!.Session.Id;
            foreach (var id in Lights.Select(c => c.Id).ToArray())
            {
                if (State?.Session.Id != session || Context.Closing) return;
                await _host.ReconcileAsync(new(Generation, id));
            }
            ReportStatus("전체 장비 상태를 확인했습니다. ON/OFF를 확인한 뒤 슬롯에 저장하세요.");
        }, () => LightSlotAccessIssue is null && Lights.Count > 0 && Lights.All(c => AllowedLight(c.Id) && !LightHasWork(c.Id)));
    }
    private void LoadLightSlotName()
    {
        _lightSlotNameEdited = false;
        Set(ref _lightSlotName, SelectedLightSlot?.Snapshot is { IsEmpty: false } slot ? slot.Name : $"슬롯 {SelectedLightSlot?.Number ?? 1}", nameof(LightSlotName));
    }
    private void RefreshLightSlots(FeatureContext previous)
    {
        var changedSession = previous.State?.Session.Id != State?.Session.Id;
        foreach (var row in LightSlots) row.Update(State?.LightSlots.SingleOrDefault(s => s.Number == row.Number));
        if (changedSession)
        {
            SelectedLightSlot = LightSlots[0]; LoadLightSlotName();
        }
        else if (!_lightSlotNameEdited) LoadLightSlotName();
        NotifyLightSlots();
    }
    private void NotifyLightSlots()
    {
        foreach (var name in new[] { nameof(LightSlotHint), nameof(SaveLightSlotHint), nameof(RestoreLightSlotHint) }) Changed(name);
        RefreshCommands();
    }
}
