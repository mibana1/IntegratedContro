using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class ScenarioService
{
    public Job RestoreLightSlot(string token, RestoreLightSlotRequest request) => _host.Change(s =>
    {
        var session = _host.Authenticate(token);
        Require(request.RequestId != Guid.Empty, "request_id_required", "요청 ID가 필요합니다.", 400);
        var fingerprint = Digest("light-slot:" + JsonSerializer.Serialize(request, JsonDefaults.Options));
        var existing = s.Jobs.SingleOrDefault(j => j.Snapshot.RequestId == request.RequestId);
        if (existing is not null)
        {
            Require(existing.Kind == JobKind.LightSlot && existing.Snapshot.RequestedBy == session.Info.UserId &&
                existing.Snapshot.SessionId == session.Info.Id && existing.RequestFingerprint == fingerprint,
                "request_id_conflict", "같은 요청 ID를 다른 내용이나 세션에서 사용할 수 없습니다.");
            return existing;
        }
        _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        var slot = _devices.ReadLightSlot(s, request.Number, request.ExpectedVersion);
        var snapshots = slot.PowerStates.Select(saved =>
        {
            var step = Resolve(s, _host.User(s, session), new(saved.Role!.Id, DeviceOperation.Power, saved.Value));
            Require(step.Role!.DeviceId == saved.Role.DeviceId && step.Role.Version == saved.Role.Version &&
                step.Target!.MatchesExecutionTarget(saved.Target!) && step.ModelDefinition is { } currentModel && saved.ModelDefinition is { } savedModel &&
                DeviceDriverRegistry.SameDefinition(currentModel, savedModel),
                "slot_target_changed", saved.Target!.Name + ": 저장 후 장비·역할·연결 설정이 변경되었습니다. 슬롯을 다시 저장하세요.");
            Require(!s.Jobs.Any(j => j.Active && j.Snapshot.Steps.Any(x => x.Target?.Id == step.Target!.Id)),
                "device_busy", step.Target!.Name + ": 진행 중 작업 또는 시나리오 예약이 있습니다. 작업 탭에서 확인하세요.");
            Require(!s.UncertainDevices.Contains(step.Target!.Id), "device_uncertain", step.Target.Name + ": 상태 대조가 필요합니다.");
            return step; // Absolute saved ON/OFF, with fresh acceptance snapshots and no toggle.
        }).ToArray();
        // Layout and admission commit together; a failed validation never partially restores the board.
        _devices.RestoreLightSlotLayout(s, slot, request.LayoutVersion);
        var snapshot = new ExecutionSnapshot(s.SiteId, ExecutionMode(snapshots), request.RequestId, session.Info.UserId,
            session.Info.UserName, session.Info.Id, session.Info.PcId, session.Info.PcName, request.Generation,
            _host.Now, _host.Now.AddMinutes(5), null, null, $"장비 슬롯 {slot.Number} · {slot.Name}", snapshots);
        var job = new Job { RequestFingerprint = fingerprint, Snapshot = snapshot, Kind = JobKind.LightSlot,
            Steps = snapshots.Select(_ => new StepRun()).ToList(), ReadyAt = _host.Now };
        s.Jobs.Add(job);
        _host.Audit(s, session.Info.UserId, "LightSlotRestored", $"slot={slot.Number}; version={slot.Version}; job={job.Id}");
        return job;
    });
}
