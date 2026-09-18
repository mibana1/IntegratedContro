using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class LightCard(DeviceConfig device) : Bindable
{
    public DeviceConfig Device { get; internal set; } = device;
    public Guid Id => Device.Id;
    public string Name => Device.Name;
    public string RoleText { get; internal set; } = "역할 미배정";
    public string PcName => Device.PcName;
    public string TargetDetail => $"{Name} · {Device.Location} · {Device.Connection.Endpoint}";
    public string Diagnostics { get; internal set; } = "";
    public string Location => Device.Location;
    public int Position { get; internal set; }
    public int? Power { get; internal set; }
    public bool IsOn => Power == 1 && !NeedsCheck;
    public bool NeedsCheck { get; internal set; }
    public bool IsEditing { get; internal set; }
    public string StateText => NeedsCheck ? "확인 필요" : Power == 1 ? "ON" : Power == 0 ? "OFF" : "상태 미확인";
    public string Hint { get; internal set; } = "";
    public string ActionText => "";
    private bool _isDragging;
    public bool IsDragging { get => _isDragging; internal set => Set(ref _isDragging, value); }
    private string _dropEdge = "";
    public string DropEdge { get => _dropEdge; internal set => Set(ref _dropEdge, value); }
    public string LastChecked { get; internal set; } = "상태 · 조회 전";
    public AsyncCommand PowerCommand { get; internal set; } = null!;
    public AsyncCommand ReadCommand { get; internal set; } = null!;
    public AsyncCommand DetailsCommand { get; internal set; } = null!;
    public AsyncCommand EarlierCommand { get; internal set; } = null!;
    public AsyncCommand LaterCommand { get; internal set; } = null!;
    internal void Refresh()
    {
        foreach (var name in new[] { nameof(Name), nameof(RoleText), nameof(PcName), nameof(TargetDetail), nameof(Position), nameof(Power), nameof(IsOn),
            nameof(Diagnostics), nameof(Location), nameof(NeedsCheck), nameof(IsEditing), nameof(StateText), nameof(Hint), nameof(ActionText), nameof(LastChecked) }) Changed(name);
    }
}

public sealed class LightGroupRow(Guid id, string name) : Bindable
{
    public Guid Id { get; } = id;
    private string _name = name;
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsDefault => Id == Guid.Empty;
    public ObservableCollection<LightCard> Cards { get; } = [];
    public string CountText => $"{Cards.Count}";
    public double LayoutWidth => Math.Clamp(Cards.Count, 2, 4) * 186 + 24;
    public bool IsEmpty => Cards.Count == 0;
    public bool Editing { get; internal set; }
    public bool CanRename { get; internal set; }
    public bool IsVisible => !IsDefault || Cards.Count > 0 || Editing;
    private bool _dropTarget;
    public bool IsDropTarget { get => _dropTarget; internal set => Set(ref _dropTarget, value); }
    public AsyncCommand RemoveCommand { get; internal set; } = null!;
    public AsyncCommand OnCommand { get; internal set; } = null!;
    public AsyncCommand OffCommand { get; internal set; } = null!;
    internal void Refresh()
    {
        foreach (var name in new[] { nameof(CountText), nameof(LayoutWidth), nameof(IsEmpty), nameof(Editing), nameof(CanRename), nameof(IsVisible) }) Changed(name);
    }
}

public sealed partial class LightingViewModel : FeatureViewModel
{
    private readonly ILightingHost _host;
    public event Action<Guid>? DeviceDetailsRequested;
    public LightingViewModel(ILightingHost host) : base(host)
    {
        _host = host;
        InitializeLighting(); InitializeLightSlots();
    }
    protected override void OnContextChanged(FeatureContext previous)
    {
        if (previous.State?.Session.Id != State?.Session.Id) _editingLightOrder = false;
        RefreshLighting(); RefreshLightSlots(previous);
    }
    public ObservableCollection<LightCard> Lights { get; } = [];
    public ObservableCollection<LightGroupRow> LightGroups { get; } = [];
    private string _newLightGroupName = "";
    public string NewLightGroupName { get => _newLightGroupName; set => Set(ref _newLightGroupName, value); }
    public bool CanArrangeLighting => _editingLightOrder && CanConfigure && !Context.Busy && !Context.Closing && State?.LightGroupsSupported == true;
    public AsyncCommand AddLightGroupCommand { get; private set; } = null!;
    public AsyncCommand AllLightsOnCommand { get; private set; } = null!;
    public AsyncCommand AllLightsOffCommand { get; private set; } = null!;
    private bool _editingLightOrder;
    private int _lightOrderVersion;
    public bool EditingLightOrder => _editingLightOrder;
    public bool HasNoLights => Lights.Count == 0;
    public string LightingSummary => $"전체 {Lights.Count}  ·  ON {Lights.Count(x => x.Power == 1 && !x.NeedsCheck)}  ·  OFF {Lights.Count(x => x.Power == 0 && !x.NeedsCheck)}  ·  확인 필요 {Lights.Count(x => x.Power is null || x.NeedsCheck)}";
    public string LightingHint => State is { LightCardsSupported: false } ? "조명 카드를 사용하려면 새 호스트 실행 파일로 업데이트하세요." :
        State is { LightGroupsSupported: false } ? "그룹과 드래그 편집에는 새 호스트가 필요합니다." :
        _editingLightOrder ? "카드를 드래그해 순서나 그룹을 바꾸고 저장하세요." :
        State is { LightBatchSupported: false } ? "일괄·그룹 제어에는 새 호스트가 필요합니다." :
        "카드를 눌러 전원을 바꿉니다. 일괄 명령은 대상 전체를 검사한 뒤 순서대로 실행합니다.";
    public AsyncCommand EditLightOrderCommand { get; private set; } = null!;
    public AsyncCommand SaveLightOrderCommand { get; private set; } = null!;
    public AsyncCommand CancelLightOrderCommand { get; private set; } = null!;
    private void InitializeLighting()
    {
        AllLightsOnCommand = Command(() => SubmitLightBatch(null, 1), () => CanSubmitLightBatch(null));
        AllLightsOffCommand = Command(() => SubmitLightBatch(null, 0), () => CanSubmitLightBatch(null));
        EditLightOrderCommand = Command(() => { _lightOrderVersion = State!.LightLayout.Version; _editingLightOrder = true; return Task.CompletedTask; },
            () => CanConfigure && State?.LightCardsSupported == true && State.LightGroupsSupported && Lights.Count > 0 && !_editingLightOrder);
        SaveLightOrderCommand = Command(async () =>
        {
            await _host.SaveLayoutAsync(new LightOrderRequest(Generation, _lightOrderVersion, Lights.Select(c => c.Id).ToArray(),
                LightGroups.Where(g => !g.IsDefault).Select(g => new LightGroup(g.Id, g.Name, g.Cards.Select(c => c.Id).ToArray())).ToArray()));
            _editingLightOrder = false; ReportStatus("조명 그룹과 순서를 저장했습니다.");
        }, () => CanConfigure && _editingLightOrder);
        AddLightGroupCommand = Command(() =>
        {
            var name = NewLightGroupName.Trim();
            if (name.Length is < 1 or > 50) throw new ArgumentException("그룹 이름을 1~50자로 입력하세요.");
            if (LightGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("같은 이름의 그룹이 있습니다.");
            LightGroups.Insert(LightGroups.Count - 1, NewGroup(Guid.NewGuid(), name));
            NewLightGroupName = ""; SyncFlatLights(); return Task.CompletedTask;
        }, () => CanConfigure && _editingLightOrder && LightGroups.Count <= 100);
        CancelLightOrderCommand = Command(() => { _editingLightOrder = false; ReportStatus("순서 편집을 취소했습니다."); return Task.CompletedTask; }, () => _editingLightOrder);
    }
    private LightCard NewLightCard(DeviceConfig row)
    {
        var card = new LightCard(row);
        card.PowerCommand = Command(() => ToggleLight(card.Id), () => LightBlockReason(card.Id) is null, register: false);
        card.ReadCommand = Command(async () =>
        {
            await _host.ReconcileAsync(new ReconcileRequest(Generation, card.Id));
            ReportStatus($"{card.Name}: 상태를 확인했습니다. 과거 작업 결과는 그대로 유지됩니다.");
        }, () => !_editingLightOrder && CanControl && AllowedLight(card.Id) && !LightHasWork(card.Id), register: false);
        card.DetailsCommand = Command(() => { DeviceDetailsRequested?.Invoke(card.Id); return Task.CompletedTask; }, register: false);
        card.EarlierCommand = Command(() => MoveLight(card, -1), () => CanConfigure && _editingLightOrder && Lights.IndexOf(card) > 0, register: false);
        card.LaterCommand = Command(() => MoveLight(card, 1), () => CanConfigure && _editingLightOrder && Lights.IndexOf(card) < Lights.Count - 1, register: false);
        return card;
    }
    private Task MoveLight(LightCard card, int delta)
    {
        var group = LightGroups.Single(g => g.Cards.Contains(card));
        var index = group.Cards.IndexOf(card);
        if (index + delta >= 0 && index + delta < group.Cards.Count)
            group.Cards.Move(index, index + delta);
        SyncFlatLights(); return Task.CompletedTask;
    }
    private LightGroupRow NewGroup(Guid id, string name)
    {
        var group = new LightGroupRow(id, name);
        group.OnCommand = Command(() => SubmitLightBatch(id, 1), () => CanSubmitLightBatch(id), register: false);
        group.OffCommand = Command(() => SubmitLightBatch(id, 0), () => CanSubmitLightBatch(id), register: false);
        group.RemoveCommand = Command(() =>
        {
            var fallback = LightGroups.Single(g => g.IsDefault);
            foreach (var card in group.Cards) fallback.Cards.Add(card);
            LightGroups.Remove(group); SyncFlatLights();
            ReportStatus("그룹을 삭제했습니다. 조명은 미분류로 옮겼습니다. 저장하면 반영됩니다.");
            return Task.CompletedTask;
        }, () => CanConfigure && _editingLightOrder && !group.IsDefault, register: false);
        return group;
    }
    private void SyncLightGroupsFromState()
    {
        var definitions = State!.LightLayout.Groups;
        var assigned = definitions.SelectMany(g => g.DeviceIds).ToHashSet();
        var all = definitions.Concat([new LightGroup(Guid.Empty, "미분류", Lights.Where(c => !assigned.Contains(c.Id)).Select(c => c.Id).ToArray())]).ToArray();
        foreach (var removed in LightGroups.Where(g => all.All(d => d.Id != g.Id)).ToArray()) LightGroups.Remove(removed);
        for (var i = 0; i < all.Length; i++)
        {
            var definition = all[i];
            var group = LightGroups.SingleOrDefault(g => g.Id == definition.Id);
            if (group is null) { group = NewGroup(definition.Id, definition.Name); LightGroups.Insert(i, group); }
            else if (LightGroups.IndexOf(group) != i) LightGroups.Move(LightGroups.IndexOf(group), i);
            group.Name = definition.Name;
            var ordered = Lights.Where(c => definition.DeviceIds.Contains(c.Id)).ToArray();
            foreach (var removed in group.Cards.Where(c => !ordered.Contains(c)).ToArray()) group.Cards.Remove(removed);
            for (var n = 0; n < ordered.Length; n++)
            {
                var card = ordered[n]; var current = group.Cards.IndexOf(card);
                if (current < 0) group.Cards.Insert(n, card);
                else if (current != n) group.Cards.Move(current, n);
            }
        }
        SyncFlatLights();
    }
    private void SyncFlatLights()
    {
        var flattened = LightGroups.SelectMany(g => g.Cards).ToArray();
        for (var i = 0; i < flattened.Length; i++)
        {
            var current = Lights.IndexOf(flattened[i]);
            if (current < 0) Lights.Insert(i, flattened[i]);
            else if (current != i) Lights.Move(current, i);
        }
        foreach (var removed in Lights.Where(c => !flattened.Contains(c)).ToArray()) Lights.Remove(removed);
    }
    public bool MoveLightToGroup(Guid deviceId, Guid groupId, Guid? beforeDeviceId)
    {
        if (!CanArrangeLighting) return false;
        var card = Lights.SingleOrDefault(c => c.Id == deviceId);
        var target = LightGroups.SingleOrDefault(g => g.Id == groupId);
        var source = LightGroups.SingleOrDefault(g => g.Cards.Any(c => c.Id == deviceId));
        if (card is null || target is null || source is null ||
            (beforeDeviceId is { } before && !target.Cards.Any(c => c.Id == before))) return false;
        if (beforeDeviceId == deviceId && source == target) return false;
        source.Cards.Remove(card);
        var index = beforeDeviceId is { } id ? target.Cards.ToList().FindIndex(c => c.Id == id) : target.Cards.Count;
        target.Cards.Insert(index, card); SyncFlatLights(); RefreshLighting(); RefreshCommands();
        return true;
    }
    private bool AllowedLight(Guid id) => State?.ControllableDeviceIds.Contains(id) == true &&
        State.Devices.Any(d => d.Id == id && d.Enabled);
    private bool LightHasWork(Guid id) => State?.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(s => s.Target?.Id == id)) == true;
    private RoleBinding? LightRole(Guid id) => State?.Roles.Where(r => r.DeviceId == id).OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
    private StateValue? LightPower(Guid id) => State?.DeviceStates.GetValueOrDefault(id)?.Values.GetValueOrDefault(DeviceOperation.Power);
    private string? LightBlockReason(Guid id)
    {
        if (State?.LightCardsSupported != true) return "호스트 업데이트 필요";
        if (_editingLightOrder) return "";
        if (!CanControl) return Context.Connected ? "사용 시작 후 조작 가능" : "호스트 연결 확인";
        if (!AllowedLight(id)) return "비활성 장비 또는 제어 권한 없음";
        if (HasPending) return "접수 여부 확인 중";
        if (State.Jobs.Any(j => j.Active && j.Kind == JobKind.Scenario && j.Snapshot.Steps.Any(s => s.Target?.Id == id))) return "시나리오 예약 · 작업 탭에서 수동 전환";
        if (LightHasWork(id)) return "명령 처리 중 · 결과 대기";
        if (State.UncertainDevices.Contains(id)) return "상태 대조 필요";
        if (LightRole(id) is null) return "역할 미배정";
        if (LightPower(id)?.Value is not (0 or 1)) return "상태 확인을 먼저 누르세요";
        return null;
    }
    private IEnumerable<LightCard> BatchCards(Guid? groupId) => groupId is null ? Lights :
        LightGroups.SingleOrDefault(g => g.Id == groupId)?.Cards ?? Enumerable.Empty<LightCard>();
    private bool CanSubmitLightBatch(Guid? groupId) => CanControl && !_editingLightOrder && !HasPending &&
        State?.LightBatchSupported == true && BatchCards(groupId).Any();
    private async Task SubmitLightBatch(Guid? groupId, int value)
    {
        var cards = BatchCards(groupId).ToArray();
        var blocked = cards.Select(c => (Card: c, Reason: LightBlockReason(c.Id))).Where(x => x.Reason is not null).ToArray();
        if (blocked.Length > 0)
            throw new InvalidOperationException("일괄 접수하지 않았습니다. " +
                string.Join(" / ", blocked.Take(4).Select(x => $"{x.Card.Name}: {x.Reason}")) +
                (blocked.Length > 4 ? $" 외 {blocked.Length - 4}개" : ""));
        var request = new LightBatchRequest(Guid.NewGuid(), Generation, value, State!.LightLayout.Version, groupId,
            cards.Select(c =>
            {
                var device = c.Device; var role = LightRole(c.Id)!; var power = LightPower(c.Id)!;
                return new LightPowerTarget(role.Id, new(c.Id, device.PcId, device.Version, role.Version, power.Value, power.At));
            }).ToArray());
        await _host.SubmitBatchAsync(request);
    }
    private async Task ToggleLight(Guid id)
    {
        // Capture the visible intention before awaiting. Host rejects stale target/state instead of retargeting.
        if (LightBlockReason(id) is { } reason) throw new InvalidOperationException(reason);
        var device = State!.Devices.Single(d => d.Id == id);
        var role = LightRole(id)!; var power = LightPower(id)!;
        var request = new SubmitRequest(Guid.NewGuid(), Generation, role.Id, DeviceOperation.Power, 1 - power.Value,
            ExpiresAfterSeconds: 30, CardPower: new(id, device.PcId, device.Version, role.Version, power.Value, power.At));
        await _host.SubmitAsync(request);
    }
    private void RefreshLighting()
    {
        if (State is null)
        {
            _editingLightOrder = false; NewLightGroupName = ""; Lights.Clear(); LightGroups.Clear();
        }
        else
        {
            var ids = State.LightLayout.DeviceIds;
            if (!_editingLightOrder)
            {
                foreach (var removed in Lights.Where(c => !ids.Contains(c.Id)).ToArray()) Lights.Remove(removed);
                for (var i = 0; i < ids.Length; i++)
                {
                    var row = State.Devices.SingleOrDefault(d => d.Id == ids[i]);
                    if (row is null) continue;
                    var card = Lights.SingleOrDefault(c => c.Id == row.Id);
                    if (card is null) { card = NewLightCard(row); Lights.Insert(i, card); }
                    else { card.Device = row; if (Lights.IndexOf(card) != i) Lights.Move(Lights.IndexOf(card), i); }
                }
            }
            if (!_editingLightOrder) SyncLightGroupsFromState();
            foreach (var group in LightGroups)
            {
                group.Editing = _editingLightOrder; group.CanRename = CanConfigure && _editingLightOrder && !group.IsDefault;
                group.Refresh(); group.OnCommand.Raise(); group.OffCommand.Raise(); group.RemoveCommand.Raise();
            }
            foreach (var card in Lights)
            {
                if (State.Devices.SingleOrDefault(d => d.Id == card.Id) is { } device) card.Device = device;
                var roles = State.Roles.Where(r => r.DeviceId == card.Id).ToArray();
                card.RoleText = roles.Length == 0 ? "역할 미배정" : string.Join(" / ", roles.Select(r => RoleChoice.From(r, State.Devices).Label));
                card.Diagnostics = State.Models.Any(m => m.Id == card.Device.ModelId && m.IsSimulation) &&
                    card.Device.Fault != VirtualFault.None ? $"장애 주입 중: {card.Device.Fault}" : "";
                var power = LightPower(card.Id);
                card.Power = power?.Value is 0 or 1 ? power.Value : null;
                card.NeedsCheck = !Context.Connected || State.UncertainDevices.Contains(card.Id);
                card.Position = Lights.IndexOf(card) + 1; card.IsEditing = _editingLightOrder;
                var reason = LightBlockReason(card.Id);
                card.Hint = reason == "역할 미배정" ? "" : reason ?? card.ActionText;
                card.LastChecked = power is null ? "상태 · 조회 전" : $"{power.Evidence} · {power.At.ToLocalTime():HH:mm:ss} 확인";
                card.Refresh();
                card.PowerCommand.Raise(); card.ReadCommand.Raise(); card.DetailsCommand.Raise();
                card.EarlierCommand.Raise(); card.LaterCommand.Raise();
            }
        }
        foreach (var name in new[] { nameof(EditingLightOrder), nameof(HasNoLights), nameof(LightingSummary), nameof(LightingHint), nameof(CanArrangeLighting) }) Changed(name);
    }
}
