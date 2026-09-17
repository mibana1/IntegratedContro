using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

internal sealed partial class DeviceExecutionService
{
    private Guid[] OrderedLightIds(DeviceStateScope s)
    {
        var ids = s.Devices.Where(d => _drivers.Models.Any(m => m.Id == d.ModelId && m.Category == DeviceCategory.Lighting))
            .Select(d => d.Id).ToArray();
        return s.LightLayout.DeviceIds.Where(ids.Contains).Concat(ids).Distinct().ToArray();
    }
    public LightLayout CurrentLightLayout(StateContext context)
    {
        var s = _host.For(context);
        var ids = OrderedLightIds(s);
        return new(s.LightLayout.Version, ids) { Groups = s.LightLayout.Groups.Select(g =>
            g with { DeviceIds = ids.Where(g.DeviceIds.Contains).ToArray() }).ToArray() };
    }
    public LightLayout SaveLightOrder(string token, LightOrderRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Require(request.ExpectedVersion == s.LightLayout.Version, "layout_changed", "다른 앱에서 순서를 변경했습니다. 편집을 취소하고 다시 시작하세요.");
        var ids = OrderedLightIds(s);
        Require(request.DeviceIds is not null && request.DeviceIds.Length == ids.Length &&
            request.DeviceIds.Distinct().Count() == ids.Length && request.DeviceIds.All(ids.Contains),
            "light_list_changed", "조명 목록이 변경되었거나 중복되었습니다. 편집을 취소하고 다시 시작하세요.");
        // Older order-only clients preserve groups; missing groups in an old database means no grouping.
        var groups = request.Groups ?? CurrentLightLayout(s).Groups;
        Require(groups.Length <= 100 && groups.All(g => g is not null && g.Id != Guid.Empty &&
            !string.IsNullOrWhiteSpace(g.Name) && g.Name.Trim().Length <= 50 && g.DeviceIds is not null),
            "invalid_light_groups", "그룹 이름은 1~50자이며 최대 100개까지 만들 수 있습니다.", 400);
        Require(groups.Select(g => g.Id).Distinct().Count() == groups.Length &&
            groups.Select(g => g.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == groups.Length,
            "duplicate_light_group", "그룹 ID와 이름은 중복될 수 없습니다.", 400);
        var members = groups.SelectMany(g => g.DeviceIds).ToArray();
        Require(members.Length == members.Distinct().Count() && members.All(ids.Contains),
            "invalid_group_members", "조명은 한 그룹에만 속할 수 있으며 등록된 조명만 배정할 수 있습니다.", 400);
        s.LightLayout = new(s.LightLayout.Version + 1, request.DeviceIds!.ToArray())
        { Groups = groups.Select(g => new LightGroup(g.Id, g.Name.Trim(), request.DeviceIds!.Where(g.DeviceIds.Contains).ToArray())).ToArray() };
        _host.Audit(s, session.Info.UserId, "LightOrderSaved", $"version={s.LightLayout.Version}; count={ids.Length}");
        return s.LightLayout;
    });
    public void ValidateCardPower(StateContext context, SubmitRequest request, StepSnapshot[] snapshots)
    {
        var s = _host.For(context);
        if (request.CardPower is not { } expected) return;
        Require(request.ScenarioId is null && request.Operation == DeviceOperation.Power && request.DelayBeforeMs == 0,
            "invalid_card_request", "조명 카드는 즉시 전원 명령만 접수합니다.", 400);
        var step = snapshots.Single();
        Require(step.Target!.Id == expected.DeviceId && step.Target!.PcId == expected.PcId &&
            step.Target!.Version == expected.DeviceVersion && step.Role!.Version == expected.RoleVersion,
            "card_target_changed", "조명의 대상 또는 역할이 변경되었습니다. 현재 상태를 확인하고 다시 누르세요.");
        Require(_drivers.Models.Any(m => m.Id == step.Target!.ModelId && m.Category == DeviceCategory.Lighting),
            "light_required", "조명 장비를 선택하세요.", 400);
        Require(!s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == expected.DeviceId)),
            "device_busy", "이 조명의 접수 작업이 남아 있습니다. 작업 결과를 먼저 확인하세요.");
        var actual = s.DeviceStates[expected.DeviceId].Values.GetValueOrDefault(DeviceOperation.Power);
        Require(expected.Power is 0 or 1 && actual is not null && actual.Value == expected.Power &&
            actual.At == expected.ObservedAt && request.Value == 1 - expected.Power,
            "light_state_changed", "조명 상태가 변경되었거나 확인되지 않았습니다. 새 상태를 확인하고 다시 누르세요.");
        // Store an absolute On/Off command and a fresh pre-send condition; never a replayable toggle.
        snapshots[0] = step with { ConditionOperation = DeviceOperation.Power, ConditionValue = expected.Power };
    }
}
