using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

internal sealed partial class DeviceExecutionService
{
    public LightSlot SaveLightSlot(string token, SaveLightSlotRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        CheckLightSlotVersion(s, request.Number, request.ExpectedVersion);
        Text(request.Name, "슬롯 이름", 50);
        Require(request.LayoutVersion == s.LightLayout.Version, "layout_changed", "그룹·순서가 변경되었습니다. 새로 고침 후 저장하세요.");
        var layout = CurrentLightLayout(s);
        Require(request.Targets is { Length: > 0 and <= 1000 } && request.Targets.All(t => t is not null && t.Expected is not null),
            "invalid_slot", "저장할 장비 1~1000개의 상태를 확인하세요.", 400);
        var targets = request.Targets!;
        Require(targets.Length == layout.DeviceIds.Length && targets.Select(t => t.Expected.DeviceId).Distinct().Count() == layout.DeviceIds.Length &&
            targets.All(t => layout.DeviceIds.Contains(t.Expected.DeviceId)), "light_list_changed", "장비 목록이 변경되었습니다. 다시 확인한 뒤 저장하세요.");
        var steps = layout.DeviceIds.Select(id =>
        {
            var target = targets.Single(t => t.Expected.DeviceId == id); var expected = target.Expected;
            var step = Resolve(s, _host.User(s, session), new(target.RoleId, DeviceOperation.Power, expected.Power));
            Require(step.Target!.Id == id && step.Target.PcId == expected.PcId &&
                step.Target.Version == expected.DeviceVersion && step.Role!.Version == expected.RoleVersion,
                "card_target_changed", "장비 또는 역할이 변경되었습니다. 다시 확인한 뒤 저장하세요.");
            Require(!s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == id)),
                "device_busy", step.Target.Name + ": 진행 중인 작업이 있습니다. 완료 후 저장하세요.");
            Require(!s.UncertainDevices.Contains(id), "device_uncertain", step.Target.Name + ": 상태 대조가 필요합니다.");
            var actual = s.DeviceStates[id].Values.GetValueOrDefault(DeviceOperation.Power);
            Require(expected.Power is 0 or 1 && actual is not null && actual.Value == expected.Power && actual.At == expected.ObservedAt,
                "light_state_changed", step.Target.Name + ": ON/OFF 상태가 변경되었거나 확인되지 않았습니다. 상태 확인 후 저장하세요.");
            return step;
        }).ToArray();
        var slot = new LightSlot(request.Number, checked(request.ExpectedVersion + 1), request.Name.Trim(), layout, steps, _host.Now);
        s.LightSlots.RemoveAll(x => x.Number == slot.Number); s.LightSlots.Add(slot);
        _host.Audit(s, session.Info.UserId, "LightSlotSaved", $"slot={slot.Number}; version={slot.Version}; count={steps.Length}");
        return slot;
    });

    public LightSlot DeleteLightSlot(string token, DeleteLightSlotRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        ReadLightSlot(s, request.Number, request.ExpectedVersion);
        var empty = new LightSlot(request.Number, checked(request.ExpectedVersion + 1), "", new(0, []), [], null);
        s.LightSlots.RemoveAll(x => x.Number == request.Number); s.LightSlots.Add(empty);
        _host.Audit(s, session.Info.UserId, "LightSlotDeleted", $"slot={empty.Number}; version={empty.Version}");
        return empty;
    });

    private static void CheckLightSlotVersion(DeviceStateScope s, int number, int version)
    {
        Require(number is >= 1 and <= LightSlot.Count, "invalid_slot", "저장 슬롯 1~6을 선택하세요.", 400);
        Require(version >= 0 && (s.LightSlots.SingleOrDefault(x => x.Number == number)?.Version ?? 0) == version,
            "version_conflict", "슬롯이 변경되었습니다. 새로 고침 후 다시 선택하세요.");
    }

    public LightSlot ReadLightSlot(StateContext context, int number, int version)
    {
        var s = _host.For(context);
        CheckLightSlotVersion(s, number, version);
        var slot = s.LightSlots.SingleOrDefault(x => x.Number == number);
        Require(slot is { IsEmpty: false }, "slot_empty", "저장된 슬롯을 선택하세요.");
        return JsonDefaults.Copy(slot!);
    }

    public void RestoreLightSlotLayout(StateContext context, LightSlot slot, int expectedVersion)
    {
        var s = _host.For(context);
        Require(s.LightLayout.Version == expectedVersion, "layout_changed", "현재 그룹·순서가 변경되었습니다. 새로 고침 후 불러오세요.");
        var current = OrderedLightIds(s);
        Require(current.Length == slot.Layout.DeviceIds.Length && current.ToHashSet().SetEquals(slot.Layout.DeviceIds),
            "light_list_changed", "저장 후 장비 목록이 변경되었습니다. 현재 장비로 슬롯을 다시 저장하세요.");
        s.LightLayout = JsonDefaults.Copy(slot.Layout) with { Version = checked(s.LightLayout.Version + 1) };
    }
}
