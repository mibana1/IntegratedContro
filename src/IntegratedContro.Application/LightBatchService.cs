using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    // Validate the entire frozen set before a single transaction accepts the sequential work.
    public Job SubmitLightBatch(string token, LightBatchRequest request) => Change(s =>
    {
        var session = Authenticate(token);
        Require(request.RequestId != Guid.Empty, "request_id_required", "요청 ID가 필요합니다.", 400);
        var fingerprint = Digest("light-batch:" + JsonSerializer.Serialize(request, JsonDefaults.Options));
        var existing = s.Jobs.SingleOrDefault(j => j.Snapshot.RequestId == request.RequestId);
        if (existing is not null)
        {
            Require(existing.IsLightBatch && existing.Snapshot.RequestedBy == session.Info.UserId &&
                existing.Snapshot.SessionId == session.Info.Id && existing.RequestFingerprint == fingerprint,
                "request_id_conflict", "같은 요청 ID를 다른 내용이나 세션에서 사용할 수 없습니다.");
            return existing;
        }
        Owner(s, token, request.Generation);
        Require(request.Value is 0 or 1, "invalid_power", "전원 값은 ON 또는 OFF여야 합니다.", 400);
        Require(request.LayoutVersion == s.LightLayout.Version, "layout_changed", "조명 그룹이나 순서가 변경되었습니다. 새로 고침 후 다시 누르세요.");
        var layout = CurrentLightLayout(s);
        var group = request.GroupId is { } id && id != Guid.Empty ? layout.Groups.SingleOrDefault(g => g.Id == id) : null;
        Require(request.GroupId is null || request.GroupId == Guid.Empty || group is not null,
            "light_group_missing", "조명 그룹이 변경되었습니다. 새로 고침 후 다시 누르세요.");
        var ids = request.GroupId is null ? layout.DeviceIds : request.GroupId == Guid.Empty
            ? layout.DeviceIds.Except(layout.Groups.SelectMany(g => g.DeviceIds)).ToArray() : group!.DeviceIds;
        Require(request.Targets is { Length: > 0 and <= 1000 } &&
            request.Targets.All(t => t is not null && t.Expected is not null),
            "invalid_batch", "조명 1~1000개의 고정 대상이 필요합니다.", 400);
        Require(request.Targets!.Length == ids.Length && request.Targets.Select(t => t.Expected.DeviceId).Distinct().Count() == ids.Length &&
            request.Targets.All(t => ids.Contains(t.Expected.DeviceId)),
            "light_list_changed", "대상 조명 목록이 변경되었거나 누락되었습니다. 새로 고침 후 다시 누르세요.");
        var snapshots = request.Targets.Select(t =>
        {
            var step = Resolve(s, User(s, session), new(t.RoleId, DeviceOperation.Power, request.Value));
            var expected = t.Expected;
            Require(step.Target!.Id == expected.DeviceId && step.Target!.PcId == expected.PcId &&
                step.Target!.Version == expected.DeviceVersion && step.Role!.Version == expected.RoleVersion,
                "card_target_changed", $"{step.Target!.Name}: 대상 또는 역할이 변경되었습니다.");
            Require(!s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == expected.DeviceId)),
                "device_busy", $"{step.Target!.Name}: 진행 중 작업 또는 시나리오 예약이 있습니다. 작업 탭에서 확인하세요.");
            Require(!s.UncertainDevices.Contains(expected.DeviceId), "device_uncertain", $"{step.Target!.Name}: 가상 상태 대조가 필요합니다.");
            var actual = s.DeviceStates[expected.DeviceId].Simulated.GetValueOrDefault(DeviceOperation.Power);
            Require(expected.Power is 0 or 1 && actual is not null && actual.Value == expected.Power && actual.At == expected.ObservedAt,
                "light_state_changed", $"{step.Target!.Name}: 상태가 변경되었거나 확인되지 않았습니다. 상태 확인 후 다시 누르세요.");
            return step with { ConditionOperation = DeviceOperation.Power, ConditionValue = expected.Power };
        }).ToArray();
        var name = $"{(request.GroupId is null ? "전체 조명" : group?.Name ?? "미분류")} · {(request.Value == 1 ? "ON" : "OFF")} ({snapshots.Length}개)";
        var snapshot = new ExecutionSnapshot(s.SiteId, "Virtual", request.RequestId, session.Info.UserId,
            session.Info.UserName, session.Info.Id, session.Info.PcId, session.Info.PcName, request.Generation,
            Now, Now.AddMinutes(5), null, null, name, snapshots);
        var job = new Job { RequestFingerprint = fingerprint, Snapshot = snapshot, Kind = JobKind.LightBatch, Steps = snapshots.Select(_ => new StepRun()).ToList(), ReadyAt = Now };
        s.Jobs.Add(job);
        Audit(s, session.Info.UserId, "LightBatchAccepted", $"job={job.Id}; request={request.RequestId}");
        return job;
    });
}
