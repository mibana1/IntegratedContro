using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class LightCard(DeviceRow device) : Bindable
{
    public DeviceRow Device { get; } = device;
    public Guid Id => Device.Id;
    public string Name => Device.Name;
    public string PcName => Device.PcName;
    public string TargetDetail => $"PC: {PcName} / PC ID: {Device.Config.PcId} / 장비 ID: {Id}";
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
    public string LastChecked { get; internal set; } = "가상 상태 · 조회 전";
    public AsyncCommand PowerCommand { get; internal set; } = null!;
    public AsyncCommand ReadCommand { get; internal set; } = null!;
    public AsyncCommand DetailsCommand { get; internal set; } = null!;
    public AsyncCommand EarlierCommand { get; internal set; } = null!;
    public AsyncCommand LaterCommand { get; internal set; } = null!;
    internal void Refresh()
    {
        foreach (var name in new[] { nameof(Name), nameof(PcName), nameof(TargetDetail), nameof(Position), nameof(Power), nameof(IsOn),
            nameof(NeedsCheck), nameof(IsEditing), nameof(StateText), nameof(Hint), nameof(ActionText), nameof(LastChecked) }) Changed(name);
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

public sealed partial class MainViewModel
{
    public ObservableCollection<LightCard> Lights { get; } = [];
    public ObservableCollection<LightGroupRow> LightGroups { get; } = [];
    private string _newLightGroupName = "";
    public string NewLightGroupName { get => _newLightGroupName; set => Set(ref _newLightGroupName, value); }
    public bool CanArrangeLighting => _editingLightOrder && CanConfigure && !_busy && !_closing && _state?.LightGroupsSupported == true;
    public AsyncCommand AddLightGroupCommand { get; private set; } = null!;
    public AsyncCommand AllLightsOnCommand { get; private set; } = null!;
    public AsyncCommand AllLightsOffCommand { get; private set; } = null!;
    private bool _editingLightOrder;
    private int _lightOrderVersion;
    private int _deviceViewIndex;
    public int DeviceViewIndex { get => _deviceViewIndex; set => Set(ref _deviceViewIndex, value); }
    public bool EditingLightOrder => _editingLightOrder;
    public bool HasNoLights => Lights.Count == 0;
    public string LightingSummary => $"전체 {Lights.Count}  ·  ON {Lights.Count(x => x.Power == 1 && !x.NeedsCheck)}  ·  OFF {Lights.Count(x => x.Power == 0 && !x.NeedsCheck)}  ·  확인 필요 {Lights.Count(x => x.Power is null || x.NeedsCheck)}";
    public string LightingHint => _state is { LightCardsSupported: false } ? "조명 카드를 사용하려면 새 호스트 실행 파일로 업데이트하세요." :
        _state is { LightGroupsSupported: false } ? "그룹과 드래그 편집에는 새 호스트가 필요합니다." :
        _editingLightOrder ? "카드를 드래그해 순서나 그룹을 바꾸고 저장하세요." :
        _state is { LightBatchSupported: false } ? "일괄·그룹 제어에는 새 호스트가 필요합니다." :
        "카드를 눌러 전원을 바꿉니다. 일괄 명령은 대상 전체를 검사한 뒤 순서대로 실행합니다.";
    public AsyncCommand EditLightOrderCommand { get; private set; } = null!;
    public AsyncCommand SaveLightOrderCommand { get; private set; } = null!;
    public AsyncCommand CancelLightOrderCommand { get; private set; } = null!;
    private void InitializeLighting()
    {
        AllLightsOnCommand = Command(() => SubmitLightBatch(null, 1), () => CanSubmitLightBatch(null));
        AllLightsOffCommand = Command(() => SubmitLightBatch(null, 0), () => CanSubmitLightBatch(null));
        EditLightOrderCommand = Command(() => { _lightOrderVersion = _state!.LightLayout.Version; _editingLightOrder = true; return Task.CompletedTask; },
            () => CanConfigure && _state?.LightCardsSupported == true && _state.LightGroupsSupported && Lights.Count > 0 && !_editingLightOrder);
        SaveLightOrderCommand = Command(async () =>
        {
            await Client.Post<LightLayout>("/api/layout/lights", new LightOrderRequest(Generation, _lightOrderVersion, Lights.Select(c => c.Id).ToArray(),
                LightGroups.Where(g => !g.IsDefault).Select(g => new LightGroup(g.Id, g.Name, g.Cards.Select(c => c.Id).ToArray())).ToArray()));
            _editingLightOrder = false; Message = "조명 그룹과 순서를 저장했습니다.";
        }, () => CanConfigure && _editingLightOrder);
        AddLightGroupCommand = Command(() =>
        {
            var name = NewLightGroupName.Trim();
            if (name.Length is < 1 or > 50) throw new ArgumentException("그룹 이름을 1~50자로 입력하세요.");
            if (LightGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("같은 이름의 그룹이 있습니다.");
            LightGroups.Insert(LightGroups.Count - 1, NewGroup(Guid.NewGuid(), name));
            NewLightGroupName = ""; SyncFlatLights(); return Task.CompletedTask;
        }, () => CanConfigure && _editingLightOrder && LightGroups.Count <= 100);
        CancelLightOrderCommand = Command(() => { _editingLightOrder = false; Message = "순서 편집을 취소했습니다."; return Task.CompletedTask; }, () => _editingLightOrder);
    }
    private LightCard NewLightCard(DeviceRow row)
    {
        var card = new LightCard(row);
        card.PowerCommand = Command(() => ToggleLight(card.Id), () => LightBlockReason(card.Id) is null);
        card.ReadCommand = Command(async () =>
        {
            await Client.Post<DeviceState>("/api/devices/reconcile", new ReconcileRequest(Generation, card.Id));
            Message = $"{card.Name}: 가상 상태를 확인했습니다. 과거 작업 결과는 그대로 유지됩니다.";
        }, () => !_editingLightOrder && CanControl && AllowedLight(card.Id) && !LightHasWork(card.Id));
        card.DetailsCommand = Command(() => { SelectedDevice = Devices.SingleOrDefault(d => d.Id == card.Id); DeviceViewIndex = 1; return Task.CompletedTask; });
        card.EarlierCommand = Command(() => MoveLight(card, -1), () => CanConfigure && _editingLightOrder && Lights.IndexOf(card) > 0);
        card.LaterCommand = Command(() => MoveLight(card, 1), () => CanConfigure && _editingLightOrder && Lights.IndexOf(card) < Lights.Count - 1);
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
        group.OnCommand = Command(() => SubmitLightBatch(id, 1), () => CanSubmitLightBatch(id));
        group.OffCommand = Command(() => SubmitLightBatch(id, 0), () => CanSubmitLightBatch(id));
        group.RemoveCommand = Command(() =>
        {
            var fallback = LightGroups.Single(g => g.IsDefault);
            foreach (var card in group.Cards) fallback.Cards.Add(card);
            LightGroups.Remove(group); SyncFlatLights();
            Message = "그룹을 삭제했습니다. 조명은 미분류로 옮겼습니다. 저장하면 반영됩니다.";
            return Task.CompletedTask;
        }, () => CanConfigure && _editingLightOrder && !group.IsDefault);
        return group;
    }
    private void SyncLightGroupsFromState()
    {
        var definitions = _state!.LightLayout.Groups;
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
        target.Cards.Insert(index, card); SyncFlatLights(); Notify();
        return true;
    }
    private bool AllowedLight(Guid id) => _state?.ControllableDeviceIds.Contains(id) == true &&
        _state.Devices.Any(d => d.Id == id && d.Enabled);
    private bool LightHasWork(Guid id) => _state?.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(s => s.Target?.Id == id)) == true;
    private RoleBinding? LightRole(Guid id) => _state?.Roles.Where(r => r.DeviceId == id).OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
    private StateValue? LightPower(Guid id) => _state?.DeviceStates.GetValueOrDefault(id)?.Simulated.GetValueOrDefault(DeviceOperation.Power);
    private string? LightBlockReason(Guid id)
    {
        if (_state?.LightCardsSupported != true) return "호스트 업데이트 필요";
        if (_editingLightOrder) return "";
        if (!CanControl) return _connected ? "사용 시작 후 조작 가능" : "호스트 연결 확인";
        if (!AllowedLight(id)) return "비활성 장비 또는 제어 권한 없음";
        if (HasPending) return "접수 여부 확인 중";
        if (_state.Jobs.Any(j => j.Active && j.Kind == JobKind.Scenario && j.Snapshot.Steps.Any(s => s.Target?.Id == id))) return "시나리오 예약 · 작업 탭에서 수동 전환";
        if (LightHasWork(id)) return "명령 처리 중 · 결과 대기";
        if (_state.UncertainDevices.Contains(id)) return "상태 대조 필요";
        if (LightRole(id) is null) return "상세 설정에서 역할 배정 필요";
        if (LightPower(id)?.Value is not (0 or 1)) return "상태 확인을 먼저 누르세요";
        return null;
    }
    private IEnumerable<LightCard> BatchCards(Guid? groupId) => groupId is null ? Lights :
        LightGroups.SingleOrDefault(g => g.Id == groupId)?.Cards ?? Enumerable.Empty<LightCard>();
    private bool CanSubmitLightBatch(Guid? groupId) => CanControl && !_editingLightOrder && !HasPending &&
        _state?.LightBatchSupported == true && BatchCards(groupId).Any();
    private async Task SubmitLightBatch(Guid? groupId, int value)
    {
        var cards = BatchCards(groupId).ToArray();
        var blocked = cards.Select(c => (Card: c, Reason: LightBlockReason(c.Id))).Where(x => x.Reason is not null).ToArray();
        if (blocked.Length > 0)
            throw new InvalidOperationException("일괄 접수하지 않았습니다. " +
                string.Join(" / ", blocked.Take(4).Select(x => $"{x.Card.Name}: {x.Reason}")) +
                (blocked.Length > 4 ? $" 외 {blocked.Length - 4}개" : ""));
        _pendingLightBatch = new(Guid.NewGuid(), Generation, value, _state!.LightLayout.Version, groupId,
            cards.Select(c =>
            {
                var device = c.Device.Config; var role = LightRole(c.Id)!; var power = LightPower(c.Id)!;
                return new LightPowerTarget(role.Id, new(c.Id, device.PcId, device.Version, role.Version, power.Value, power.At));
            }).ToArray());
        Notify(); await SendPending();
    }
    private async Task ToggleLight(Guid id)
    {
        // Capture the visible intention before awaiting. Host rejects stale target/state instead of retargeting.
        if (LightBlockReason(id) is { } reason) throw new InvalidOperationException(reason);
        var device = _state!.Devices.Single(d => d.Id == id);
        var role = LightRole(id)!; var power = LightPower(id)!;
        _pending = new(Guid.NewGuid(), Generation, role.Id, DeviceOperation.Power, 1 - power.Value,
            ExpiresAfterSeconds: 30, CardPower: new(id, device.PcId, device.Version, role.Version, power.Value, power.At));
        Notify(); await SendPending();
    }
    private void RefreshLighting()
    {
        if (_state is null)
        {
            Lights.Clear(); LightGroups.Clear();
        }
        else
        {
            var ids = _state.LightLayout.DeviceIds;
            if (!_editingLightOrder)
            {
                foreach (var removed in Lights.Where(c => !ids.Contains(c.Id)).ToArray()) Lights.Remove(removed);
                for (var i = 0; i < ids.Length; i++)
                {
                    var row = Devices.SingleOrDefault(d => d.Id == ids[i]);
                    if (row is null) continue;
                    var card = Lights.SingleOrDefault(c => c.Id == row.Id);
                    if (card is null) { card = NewLightCard(row); Lights.Insert(i, card); }
                    else if (Lights.IndexOf(card) != i) Lights.Move(Lights.IndexOf(card), i);
                }
            }
            if (!_editingLightOrder) SyncLightGroupsFromState();
            foreach (var group in LightGroups)
            {
                group.Editing = _editingLightOrder; group.CanRename = CanConfigure && _editingLightOrder && !group.IsDefault;
                group.Refresh();
            }
            foreach (var card in Lights)
            {
                var power = LightPower(card.Id);
                card.Power = power?.Value is 0 or 1 ? power.Value : null;
                card.NeedsCheck = !_connected || _state.UncertainDevices.Contains(card.Id);
                card.Position = Lights.IndexOf(card) + 1; card.IsEditing = _editingLightOrder;
                card.Hint = LightBlockReason(card.Id) ?? card.ActionText;
                card.LastChecked = power is null ? "가상 상태 · 조회 전" : $"가상 상태 · {power.At.ToLocalTime():HH:mm:ss} 확인";
                card.Refresh();
            }
        }
        foreach (var name in new[] { nameof(EditingLightOrder), nameof(HasNoLights), nameof(LightingSummary), nameof(LightingHint), nameof(CanArrangeLighting) }) Changed(name);
    }
}
