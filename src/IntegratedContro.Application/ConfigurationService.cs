using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public AccountView CreateAccount(string token, CreateAccountRequest request) => _host.CreateAccount(token, request);
    public bool UpdateAccount(string token, UpdateAccountRequest request) => _host.UpdateAccount(token, request);
    public ScenarioDefinition SaveScenario(string token, ScenarioRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        Text(request.Name, "시나리오 이름");
        Require(request.Id != Guid.Empty && request.Steps is { Length: >= 1 and <= 100 },
            "invalid_scenario", "시나리오는 1~100단계로 구성하세요.", 400);
        var steps = request.Steps.ToArray();
        foreach (var step in steps) Resolve(s, User(s, session), step);
        var old = s.Scenarios.SingleOrDefault(x => x.Id == request.Id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "시나리오 설정을 다시 조회하세요.");
        var definition = new ScenarioDefinition(request.Id, request.Name.Trim(),
            checked(Math.Max(old?.Version ?? 0, s.DeletedScenarioVersions.GetValueOrDefault(request.Id)) + 1), steps);
        s.Scenarios.RemoveAll(x => x.Id == definition.Id); s.Scenarios.Add(definition);
        Audit(s, session.Info.UserId, "ScenarioSaved", $"scenario={definition.Id}; v={definition.Version}");
        return definition;
    });
    public bool DeleteScenario(string token, DeleteScenarioRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        Require(request.Id != Guid.Empty && request.ExpectedVersion > 0,
            "invalid_scenario", "삭제할 시나리오를 확인하세요.", 400);
        var definition = s.Scenarios.SingleOrDefault(x => x.Id == request.Id);
        Require(definition is not null && definition.Version == request.ExpectedVersion,
            "version_conflict", "시나리오가 변경되었거나 삭제되었습니다. 다시 조회하세요.");
        Require(!s.Jobs.Any(j => j.Active && j.Snapshot.ScenarioId == request.Id),
            "scenario_in_use", "이 시나리오의 작업이 진행 중입니다. 작업·교대에서 완료를 확인하거나 취소한 뒤 삭제하세요.");
        // Capture the definition name in the audit before removing it. Frozen job history remains intact.
        Audit(s, session.Info.UserId, "ScenarioDeleted", $"scenario={definition!.Id}; v={definition.Version}");
        s.DeletedScenarioVersions[definition.Id] = definition.Version;
        s.Scenarios.Remove(definition);
        return true;
    });
    private StepSnapshot Resolve(HostState s, Account user, ScenarioStep step, RoleBinding? overrideRole = null)
    {
        Require(step is not null && Enum.IsDefined(step.Kind), "invalid_step", "단계 종류를 확인하세요.", 400);
        Require(step!.DelayBeforeMs is >= 0 and <= 3600000 && step.TimeoutMs >= 100 &&
            step.TimeoutMs <= (step.Kind == ScenarioStepKind.DeviceCommand ? 30000 : 3600000) && Enum.IsDefined(step.OnFailure),
            "invalid_timing", "단계 전 대기는 최대 1시간, 제한시간은 명령 0.1~30초 / 조건·표시 0.1초~1시간입니다.", 400);
        if (step.Kind == ScenarioStepKind.DisplayLayout)
        {
            Require(string.IsNullOrEmpty(step.RoleId) && step.ConditionOperation is null && step.ConditionValue is null &&
                step.LayoutId is not null, "invalid_step", "배치 표시에는 저장 배치만 지정하세요.", 400);
            Require(HiperwallPermission(user), "hiperwall_scope", "배치 표시에는 전체 장비 제어 권한이 필요합니다.", 403);
            Require(_hiperwall is IHiperwallWriter && _credentials is not null && s.Hiperwall is not null,
                "hiperwall_unavailable", "Hiperwall 연결 설정이 필요합니다.");
            var layout = s.HiperwallLayouts.SingleOrDefault(l => l.Id == step.LayoutId);
            Require(layout is not null && layout.ConfigurationVersion == s.Hiperwall!.Version, "layout_changed",
                "저장 배치가 없거나 연결 설정이 변경되었습니다. 배치를 검토·저장하세요.");
            ValidatePlacements(layout!.Placements);
            Require(layout.Duration.IsValid, "invalid_duration", "배치 표시 시간을 확인하세요.", 400);
            return new(null, null, default, 0, "", step.DelayBeforeMs, step.TimeoutMs, step.OnFailure, null, null)
            { Kind = step.Kind, Display = new(JsonDefaults.Copy(layout), s.Hiperwall!.Endpoint, Guid.NewGuid()) };
        }
        return _devices.Resolve(s, user, step, overrideRole);
    }
}
