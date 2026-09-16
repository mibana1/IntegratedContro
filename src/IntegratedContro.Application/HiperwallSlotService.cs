using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public async Task<HiperwallSlot> SaveHiperwallSlotAsync(string token, SaveHiperwallSlotRequest request, CancellationToken ct)
    {
        HiperwallConfiguration config;
        lock (_gate)
        {
            DisplayOwner(token, request.Generation, request.ConfigurationVersion);
            CheckSlotVersion(_state, request.Number, request.ExpectedVersion);
            config = _state.Hiperwall!;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(config.TimeoutMs);
        var secret = config.CredentialId is { } key ? _credentials!.Read(key) : null;
        HiperwallReading reading;
        try { reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token).ConfigureAwait(false); }
        finally { secret = null; }
        RequireWritableInventory(reading);
        Require(HiperwallEditing.Revision(reading.Instances.Items) == request.ExpectedRevision,
            "hiperwall_revision", "현재 콘텐츠가 변경되었습니다. 목록을 새로 고친 뒤 저장하세요.");
        Require(reading.Instances.Items.Length is > 0 and <= 100, "invalid_layout", "슬롯에는 현재 콘텐츠 1~100개를 저장할 수 있습니다.", 400);
        var placements = reading.Instances.Items.Select(item =>
        {
            Require(HiperwallGeometry.TryInstance(item, out var rect, out _), "hiperwall_geometry",
                "위치·크기·회전을 확인할 수 없는 콘텐츠가 있습니다. 저장 전 현재 상태를 확인하세요.");
            var uuid = item.Fields.GetValueOrDefault("content.uuid");
            var zone = HiperwallGeometry.ResolveInstanceZone(item, reading.Zones.Items)?.Id;
            Require(zone is not null, "hiperwall_zone_missing",
                $"Zone을 확인할 수 없는 콘텐츠: {item.Name} (인스턴스 ID: {item.Id ?? "없음"}). Controller의 Zone 정보와 콘텐츠 위치를 확인하세요.");
            var audio = HiperwallEditing.TryAudio(item, out var volume, out var muted);
            var p = new HiperwallPlacement(string.IsNullOrEmpty(uuid) ? "name" : "uuid",
                string.IsNullOrEmpty(uuid) ? item.Name : uuid, zone!, HiperwallLayout.From(rect), audio ? volume : null, audio ? muted : null);
            ValidateDisplayContent(new(HiperwallEditAction.Open, Selector: p.Selector, ContentValue: p.ContentValue, ZoneId: p.ZoneId), reading);
            return p;
        }).ToArray();
        ValidatePlacements(placements);
        return Change(s =>
        {
            ct.ThrowIfCancellationRequested();
            var session = DisplayOwner(token, request.Generation, request.ConfigurationVersion);
            CheckSlotVersion(s, request.Number, request.ExpectedVersion);
            var slot = new HiperwallSlot(request.Number, checked(request.ExpectedVersion + 1), config.Version, placements, Now);
            s.HiperwallSlots.RemoveAll(x => x.Number == slot.Number); s.HiperwallSlots.Add(slot);
            Audit(s, session.Info.UserId, "HiperwallSlotSaved", $"slot={slot.Number}; version={slot.Version}; count={placements.Length}");
            return slot;
        });
    }
    public bool DeleteHiperwallSlot(string token, DeleteHiperwallSlotRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation);
        Require(HiperwallPermission(User(s, session)), "hiperwall_scope", "전체 장비 제어 권한이 필요합니다.", 403);
        CheckSlotVersion(s, request.Number, request.ExpectedVersion);
        var slot = s.HiperwallSlots.SingleOrDefault(x => x.Number == request.Number);
        Require(slot is { IsEmpty: false }, "slot_empty", "저장된 슬롯을 선택하세요.");
        s.HiperwallSlots.Remove(slot!);
        s.HiperwallSlots.Add(new(request.Number, checked(request.ExpectedVersion + 1), 0, [], null));
        Audit(s, session.Info.UserId, "HiperwallSlotDeleted", $"slot={request.Number}; version={request.ExpectedVersion}");
        return true;
    });
    private static void CheckSlotVersion(HostState s, int number, int version)
    {
        Require(number is >= 1 and <= HiperwallSlot.Count, "invalid_slot", "저장 슬롯을 선택하세요.", 400);
        Require(version >= 0 && (s.HiperwallSlots.SingleOrDefault(x => x.Number == number)?.Version ?? 0) == version,
            "version_conflict", "슬롯이 다른 화면에서 변경되었습니다. 최신 슬롯을 확인한 뒤 다시 조작하세요.");
    }
    private HiperwallSlot ReadRestoreSlot(HiperwallEditRequest request)
    {
        Require(request.SlotNumber is not null && request.SlotVersion is not null,
            "invalid_slot", "불러올 슬롯과 버전이 필요합니다.", 400);
        CheckSlotVersion(_state, request.SlotNumber!.Value, request.SlotVersion!.Value);
        var slot = _state.HiperwallSlots.SingleOrDefault(x => x.Number == request.SlotNumber);
        Require(slot is { IsEmpty: false } && slot.ConfigurationVersion == request.ConfigurationVersion,
            "slot_changed", "슬롯이 비어 있거나 Controller 설정이 변경되었습니다. 현재 연결에서 다시 저장하세요.");
        return JsonDefaults.Copy(slot!);
    }
    private static HiperwallWireCommand[] BuildSlotRestore(HiperwallEditRequest request, HiperwallSlot slot, HiperwallReading reading)
    {
        RequireWritableInventory(reading); ValidatePlacements(slot.Placements);
        Require(request.InstanceId is null && request.Selector is null && request.ContentValue is null &&
            request.ZoneId is null && request.Layout is null && request.Volume is null && request.Muted is null,
            "invalid_request", "슬롯 불러오기에는 슬롯과 현재 목록만 지정하세요.", 400);
        Require(HiperwallEditing.Revision(reading.Instances.Items) == request.ExpectedRevision,
            "hiperwall_revision", "현재 콘텐츠가 변경되었습니다. 목록을 새로 고친 뒤 불러오세요.");
        Require(reading.Instances.Items.Length <= 1000 && reading.Instances.Items.All(i => !string.IsNullOrWhiteSpace(i.Id)) &&
            reading.Instances.Items.Select(i => i.Id).Distinct().Count() == reading.Instances.Items.Length,
            "hiperwall_inventory", "현재 콘텐츠의 고유 인스턴스 목록을 확인하세요.");
        var opens = slot.Placements.Select((p, i) => new HiperwallWireCommand(HiperwallEditAction.Open,
            $"integrated-{request.RequestId:N}-{i}", p.Selector, p.ContentValue, p.ZoneId, p.Layout, p.Volume, p.Muted)).ToArray();
        foreach (var command in opens) ValidateDisplayContent(command, reading);
        return reading.Instances.Items.Select(i => new HiperwallWireCommand(HiperwallEditAction.Close, i.Id)).Concat(opens).ToArray();
    }
    private static void ValidateSlotRestoreStep(HiperwallEditReceipt receipt, int index, HiperwallReading reading)
    {
        RequireWritableInventory(reading);
        Require(receipt.Steps.Take(index).All(s => s.State == HiperwallSendState.Acknowledged),
            "slot_restore_stopped", "앞 단계가 완료되지 않아 불러오기를 중단합니다.");
        var remaining = receipt.Steps.Skip(index).Where(s => s.Command.Action == HiperwallEditAction.Close).ToArray();
        var opened = receipt.Steps.Take(index).Where(s => s.Command.Action == HiperwallEditAction.Open).ToArray();
        var expectedIds = remaining.Concat(opened).Select(s => s.Command.InstanceId).Order(StringComparer.Ordinal);
        Require(reading.Instances.Items.Select(i => i.Id).Order(StringComparer.Ordinal).SequenceEqual(expectedIds),
            "hiperwall_revision", "불러오는 도중 현재 콘텐츠가 변경되어 후속 전송을 중단합니다.");
        foreach (var step in remaining)
        {
            var item = reading.Instances.Items.Single(i => i.Id == step.Command.InstanceId);
            Require(step.ExpectedTargetRevision == HiperwallEditing.Revision([item]), "hiperwall_revision",
                "불러오는 도중 닫을 콘텐츠가 변경되어 후속 전송을 중단합니다.");
        }
        foreach (var step in opened)
        {
            var item = reading.Instances.Items.Single(i => i.Id == step.Command.InstanceId);
            Require(MatchesDisplaySource(item, step.Command) &&
                HiperwallGeometry.ResolveInstanceZone(item, reading.Zones.Items)?.Id == step.Command.ZoneId &&
                HiperwallGeometry.TryInstance(item, out var rect, out _) && HiperwallLayout.From(rect) == step.Command.Layout,
                "hiperwall_revision", "불러온 콘텐츠가 외부에서 변경되어 후속 전송을 중단합니다.");
        }
        // Validate every remaining source before removing any more of the existing view.
        foreach (var step in receipt.Steps.Skip(index).Where(s => s.Command.Action == HiperwallEditAction.Open))
            ValidateDisplayContent(step.Command, reading);
    }
    private void RequireNoSlotRestore()
    {
        Require(!_state.HiperwallEdits.Any(e => e.Active && e.Request.Action == HiperwallEditAction.RestoreSlot),
            "hiperwall_busy", "저장 슬롯을 불러오는 중입니다. 완료 후 다시 조작하세요.");
    }
}
