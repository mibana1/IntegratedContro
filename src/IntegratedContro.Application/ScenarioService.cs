using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class ScenarioService
{
    private readonly IScenarioStateAccess _host;
    private readonly IDeviceScenarioOperations _devices;
    private readonly IScenarioDisplayOperations _displays;
    private readonly IScenarioJobLifecycle _jobs;
    private int _dispatching;
    internal ScenarioService(IScenarioStateAccess host, IDeviceScenarioOperations devices,
        IScenarioDisplayOperations displays, IScenarioJobLifecycle jobs)
    { _host = host; _devices = devices; _displays = displays; _jobs = jobs; }
    private StepSnapshot Resolve(ScenarioStateScope state, AccountView user, ScenarioStep step)
    {
        ValidateStep(step);
        return step.Kind == ScenarioStepKind.DisplayLayout
            ? _displays.ResolveDisplay(state, user, step) : _devices.Resolve(state, user, step);
    }
    private string? Revalidate(ScenarioStateScope state, Job job, StepSnapshot step) =>
        RevalidateJob(state.SiteId, state.Scenarios, job, _host.Now) ?? (step.Kind == ScenarioStepKind.DisplayLayout
            ? _displays.RevalidateDisplay(state, job, step) : _devices.RevalidateTarget(state, job, step));
    public ScenarioDefinition SaveScenario(string token, ScenarioRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Text(request.Name, "시나리오 이름");
        Require(request.Id != Guid.Empty && request.Steps is { Length: >= 1 and <= 100 },
            "invalid_scenario", "시나리오는 1~100단계로 구성하세요.", 400);
        var steps = request.Steps.ToArray();
        foreach (var step in steps) Resolve(s, _host.User(s, session), step);
        var old = s.Scenarios.SingleOrDefault(x => x.Id == request.Id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "시나리오 설정을 다시 조회하세요.");
        var definition = new ScenarioDefinition(request.Id, request.Name.Trim(),
            checked(Math.Max(old?.Version ?? 0, s.DeletedScenarioVersions.GetValueOrDefault(request.Id)) + 1), steps);
        s.Scenarios.RemoveAll(x => x.Id == definition.Id); s.Scenarios.Add(definition);
        _host.Audit(s, session.Info.UserId, "ScenarioSaved", $"scenario={definition.Id}; v={definition.Version}");
        return definition;
    });
    public bool DeleteScenario(string token, DeleteScenarioRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Require(request.Id != Guid.Empty && request.ExpectedVersion > 0,
            "invalid_scenario", "삭제할 시나리오를 확인하세요.", 400);
        var definition = s.Scenarios.SingleOrDefault(x => x.Id == request.Id);
        Require(definition is not null && definition.Version == request.ExpectedVersion,
            "version_conflict", "시나리오가 변경되었거나 삭제되었습니다. 다시 조회하세요.");
        Require(!s.Jobs.Any(j => j.Active && j.Snapshot.ScenarioId == request.Id),
            "scenario_in_use", "이 시나리오의 작업이 진행 중입니다. 작업·교대에서 완료를 확인하거나 취소한 뒤 삭제하세요.");
        // Capture the definition name in the audit before removing it. Frozen job history remains intact.
        _host.Audit(s, session.Info.UserId, "ScenarioDeleted", $"scenario={definition!.Id}; v={definition.Version}");
        s.DeletedScenarioVersions[definition.Id] = definition.Version;
        s.Scenarios.Remove(definition);
        return true;
    });
}
