using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private static bool UsesHiperwall(Job job) => job.Snapshot.Steps.Any(s => s.Kind == ScenarioStepKind.ShowLayout);
    private static bool CanControlStep(Account user, StepSnapshot step) => step.Kind == ScenarioStepKind.ShowLayout
        ? HiperwallPermission(user) : step.Target is { } target && CanControl(user, target.Id);
    private static IEnumerable<Guid> DeviceTargets(IEnumerable<StepSnapshot> steps) =>
        steps.Where(s => s.Target is not null).Select(s => s.Target!.Id);
    private static string ExecutionMode(IEnumerable<StepSnapshot> steps) => steps.Any(s => s.Kind == ScenarioStepKind.ShowLayout) ? "Mixed" : "Virtual";

    public async Task<SavedHiperwallLayout> CaptureHiperwallLayoutAsync(string token, CaptureHiperwallLayoutRequest request, CancellationToken ct)
    {
        HiperwallConfiguration config;
        lock (_gate)
        {
            Healthy(); Owner(_state, token, request.Generation); Admin(_state, token);
            Require(_hiperwall is not null && _credentials is not null, "hiperwall_unavailable", "Hiperwall 조회 기능이 필요합니다.");
            Require(_state.Hiperwall?.Version == request.ConfigurationVersion, "hiperwall_settings_changed", "연결 설정을 다시 확인하세요.");
            config = _state.Hiperwall!;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(config.TimeoutMs);
        var secret = config.CredentialId is { } reference ? _credentials!.Read(reference) : null;
        HiperwallReading reading;
        try { reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token); }
        finally { secret = null; }
        Require(reading.State == HiperwallConnectionState.Connected && reading.Instances.State == HiperwallListState.Available &&
            reading.Contents.State == HiperwallListState.Available && reading.Zones.State == HiperwallListState.Available,
            "hiperwall_inventory", "현재 인스턴스·Contents·Zone 목록을 확인해야 저장할 수 있습니다.");
        Require(request.ExpectedInstancesRevision == HiperwallEditing.Revision(reading.Instances.Items),
            "hiperwall_revision", "LIVE 배치가 변경되었습니다. 다시 조회하고 저장하세요.");
        Require(reading.Instances.Items.Length is > 0 and <= 100, "invalid_layout", "배치는 1~100개의 열린 콘텐츠로 구성하세요.", 400);
        var entries = reading.Instances.Items.Select(item =>
        {
            Require(HiperwallGeometry.TryInstance(item, out var bounds, out _), "hiperwall_geometry", $"{item.Name}: 저장할 위치·크기를 확인할 수 없습니다.");
            var uuid = item.Fields.GetValueOrDefault("content.uuid");
            var selector = string.IsNullOrWhiteSpace(uuid) ? "name" : "uuid";
            var value = selector == "uuid" ? uuid! : item.Name;
            var zone = item.Fields.GetValueOrDefault("content.zone");
            if (string.IsNullOrWhiteSpace(zone)) zone = request.FallbackZoneId;
            var volume = HiperwallEditing.TryAudio(item, out var v, out var muted) ? v : (int?)null;
            var entry = new HiperwallLayoutEntry(selector, value, item.Name, zone ?? "", HiperwallLayout.From(bounds), volume, volume is null ? null : muted);
            ValidateLayoutEntry(entry, reading);
            return entry;
        }).ToArray();
        return Change(s =>
        {
            ct.ThrowIfCancellationRequested(); var session = Owner(s, token, request.Generation); Admin(s, token);
            Require(s.Hiperwall == config, "hiperwall_settings_changed", "저장 중 연결 설정이 변경되었습니다.");
            Text(request.Name, "배치 이름");
            Require(request.Id != Guid.Empty, "invalid_layout", "배치 ID가 필요합니다.", 400);
            var old = s.HiperwallLayouts.SingleOrDefault(l => l.Id == request.Id);
            Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "저장 배치를 다시 불러오세요.");
            var saved = new SavedHiperwallLayout(request.Id, request.Name.Trim(), (old?.Version ?? 0) + 1,
                config.Version, config.Endpoint, entries);
            s.HiperwallLayouts.RemoveAll(l => l.Id == request.Id); s.HiperwallLayouts.Add(saved);
            Audit(s, session.Info.UserId, "HiperwallLayoutSaved", $"배치 {saved.Name} v{saved.Version} · {entries.Length}개 저장 / LIVE 변경 없음");
            return saved;
        });
    }
    public bool DeleteHiperwallLayout(string token, DeleteHiperwallLayoutRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token);
        var layout = s.HiperwallLayouts.SingleOrDefault(l => l.Id == request.Id);
        Require(layout is not null && layout.Version == request.ExpectedVersion, "version_conflict", "저장 배치를 다시 조회하세요.");
        Require(!s.Scenarios.Any(d => d.Steps.Any(step => step.SavedLayoutId == request.Id)), "layout_in_use", "시나리오에서 사용 중인 배치입니다. 정의의 참조를 먼저 변경하세요.");
        s.HiperwallLayouts.RemoveAll(l => l.Id == request.Id);
        Audit(s, session.Info.UserId, "HiperwallLayoutDeleted", $"배치 {layout!.Name} 삭제 / 접수 작업 snapshot 유지");
        return true;
    });
    private static void ValidateLayoutEntry(HiperwallLayoutEntry entry, HiperwallReading reading)
    {
        Require(entry.Bounds.IsValid && entry.Volume is not (< 0 or > 100), "invalid_layout", "배치의 위치·크기·음량을 확인하세요.", 400);
        Require(entry.Selector is "uuid" or "name", "invalid_selector", "배치 콘텐츠의 UUID 또는 원문 이름이 필요합니다.", 400);
        Text(entry.ContentValue, "콘텐츠 식별자", 4096); Text(entry.ZoneId, "Zone ID", 4096);
        var contents = reading.Contents.Items.Where(i => entry.Selector == "uuid" ? i.Id == entry.ContentValue : i.Name == entry.ContentValue).ToArray();
        Require(reading.Contents.State == HiperwallListState.Available && contents.Length == 1 &&
            (entry.Selector == "uuid" || contents[0].Id is null), "hiperwall_content_missing", "배치 콘텐츠가 없거나 원문 이름이 중복됩니다.");
        Require(reading.Zones.State == HiperwallListState.Available && reading.Zones.Items.Count(z => z.Id == entry.ZoneId) == 1,
            "hiperwall_zone_missing", "배치의 Zone을 확인할 수 없습니다. Zone이 없는 인스턴스는 선택 Zone을 지정한 뒤 저장하세요.");
    }
    private StepSnapshot ResolveLayout(HostState s, Account user, ScenarioStep step)
    {
        Require(HiperwallPermission(user), "hiperwall_scope", "저장 배치 표시는 전체 장비 제어 권한이 필요합니다.", 403);
        Require(_hiperwall is IHiperwallWriter && _credentials is not null, "hiperwall_unavailable", "Hiperwall 표시를 지원하는 호스트가 필요합니다.");
        var layout = s.HiperwallLayouts.SingleOrDefault(l => l.Id == step.SavedLayoutId);
        Require(layout is not null, "layout_missing", "저장 배치를 선택하세요.", 400);
        Require(s.Hiperwall?.Version == layout!.ConfigurationVersion && s.Hiperwall.Endpoint == layout.Endpoint,
            "hiperwall_settings_changed", "배치의 Controller 설정이 변경되었습니다. 현재 LIVE를 확인하고 배치를 다시 저장하세요.");
        Require(string.IsNullOrEmpty(step.RoleId) && step.ConditionOperation is null && step.ConditionValue is null,
            "invalid_scenario", "배치 표시에는 장비 역할·조건을 함께 지정하지 않습니다.", 400);
        return new(null, null, DeviceOperation.Power, 0, "", step.DelayBeforeMs, step.TimeoutMs, step.OnFailure, null, null)
        { Kind = ScenarioStepKind.ShowLayout, SavedLayout = JsonDefaults.Copy(layout), DisplayReceiptId = Guid.NewGuid() };
    }
    private string? ValidateLayoutDispatch(HostState state, Job job, StepSnapshot step)
    {
        if (!state.Accounts.Any(a => a.Id == job.Snapshot.RequestedBy && HiperwallPermission(a))) return "원 요청자 Hiperwall 권한 회수";
        var layout = step.SavedLayout;
        if (layout is null || step.DisplayReceiptId is null || _hiperwall is not IHiperwallWriter || _credentials is null) return "저장 배치 snapshot/어댑터 오류";
        if (state.Hiperwall?.Version != layout.ConfigurationVersion || state.Hiperwall.Endpoint != layout.Endpoint) return "Controller 연결 설정 변경";
        return null;
    }
    private void BeginLayoutDisplay(HostState state, Job job, int index)
    {
        var snapshot = job.Snapshot.Steps[index]; var layout = snapshot.SavedLayout!;
        var id = snapshot.DisplayReceiptId!.Value;
        var request = new HiperwallEditRequest(id, job.Snapshot.LeaseGeneration, layout.ConfigurationVersion, HiperwallEditAction.DisplayLayout);
        var receipt = new HiperwallEditReceipt
        {
            ParentJobId = job.Id, ParentStepIndex = index, Request = request, Endpoint = layout.Endpoint, AcceptedAt = Now,
            Requester = new(job.Snapshot.SessionId, job.Snapshot.RequestedBy, job.Snapshot.RequesterName,
                state.Accounts.Single(a => a.Id == job.Snapshot.RequestedBy).Role, job.Snapshot.ClientPcId, job.Snapshot.ClientPcName),
            Steps = layout.Entries.Select((entry, n) => new HiperwallEditStep
            {
                Command = new(HiperwallEditAction.Open, $"integrated-{id:N}-{n}", entry.Selector, entry.ContentValue,
                    entry.ZoneId, entry.Bounds, entry.Volume, entry.Muted)
            }).ToList()
        };
        state.HiperwallEdits.Add(receipt);
        var run = job.Steps[index]; run.Status = StepStatus.Dispatching; run.WaitStartedAt = Now;
        run.WaitDeadline = Now.AddMilliseconds(snapshot.TimeoutMs); run.Result = $"저장 배치 {layout.Name} v{layout.Version} · 전송 대기";
        job.Status = JobStatus.Running; job.Result = run.Result;
        Audit(state, job.Snapshot.RequestedBy, "HiperwallLayoutAccepted", $"job={job.Id}; 배치 {layout.Name} v{layout.Version}; request={id}");
    }
}
