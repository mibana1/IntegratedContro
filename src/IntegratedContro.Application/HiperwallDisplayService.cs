using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;

namespace IntegratedContro.Application;

internal sealed partial class HiperwallService
{
    // LIVE edits and display/cleanup writes share one host-owned execution lane.
    private readonly SemaphoreSlim _hiperwallWrites = new(1, 1);
    public HiperwallDisplayView GetHiperwallDisplays(string token)
    {
        using (_host.Open())
        {
            HiperwallReaderSession(token);
            var recent = _host.Current.HiperwallDisplays.TakeLast(100).Select(j => j.Request.RequestId).ToHashSet();
            return JsonDefaults.Copy(new HiperwallDisplayView(_host.Current.HiperwallLayouts.ToArray(),
                _host.Current.HiperwallDisplays.Where(j => j.Outstanding || recent.Contains(j.Request.RequestId)).Reverse().ToArray()));
        }
    }
    private Session DisplayOwner(string token, long generation, int version) =>
        HiperwallOwner(token, new(Guid.NewGuid(), generation, version, HiperwallEditAction.Open));
    private static void ValidatePlacements(HiperwallPlacement[]? placements)
    {
        Require(placements is { Length: > 0 and <= 100 }, "invalid_layout", "배치는 1~100개 콘텐츠를 지원합니다.", 400);
        foreach (var p in placements!)
        {
            Require(p is not null && p.Selector is "uuid" or "name" && p.Layout is { IsValid: true } && p.Volume is not (< 0 or > 100),
                "invalid_layout", "콘텐츠 식별자·유한한 좌표·양수 크기·음량을 확인하세요.", 400);
            Text(p!.ContentValue, "콘텐츠 식별자", 4096); Text(p.ZoneId, "Zone ID", 4096);
        }
    }
    public SavedHiperwallLayout SaveHiperwallLayout(string token, SaveHiperwallLayoutRequest request) => _host.Change(s =>
    {
        var session = DisplayOwner(token, request.Generation, request.ConfigurationVersion);
        Require(request.Id != Guid.Empty && request.Duration is { IsValid: true }, "invalid_layout", "배치 ID와 표시 시간을 확인하세요.", 400);
        Text(request.Name, "배치 이름", 120); ValidatePlacements(request.Placements);
        var old = s.HiperwallLayouts.SingleOrDefault(l => l.Id == request.Id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "배치가 변경되었습니다. 다시 불러오세요.");
        Require(old is not null || s.HiperwallLayouts.Count < 500, "layout_limit", "저장 배치는 최대 500개입니다.");
        var layout = new SavedHiperwallLayout(request.Id, request.ExpectedVersion + 1, request.ConfigurationVersion,
            request.Name.Trim(), JsonDefaults.Copy(request.Placements), request.Duration, _host.Now);
        s.HiperwallLayouts.RemoveAll(l => l.Id == layout.Id); s.HiperwallLayouts.Add(layout);
        _host.Audit(s, session.Info.UserId, "HiperwallLayoutSaved", $"layout={layout.Id}; version={layout.Version}");
        return layout;
    });
    public bool DeleteHiperwallLayout(string token, DeleteHiperwallLayoutRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation);
        Require(HiperwallPermission(_host.User(s, session)), "hiperwall_scope", "전체 장비 제어 권한이 필요합니다.", 403);
        var old = s.HiperwallLayouts.SingleOrDefault(l => l.Id == request.Id);
        Require(old is not null && old.Version == request.ExpectedVersion, "version_conflict", "배치가 변경되었거나 삭제되었습니다.");
        s.HiperwallLayouts.Remove(old!);
        _host.Audit(s, session.Info.UserId, "HiperwallLayoutDeleted", $"layout={request.Id}; 접수된 표시 유지");
        return true;
    });
    public async Task<HiperwallDisplayJob> DisplayHiperwallAsync(string token, HiperwallDisplayRequest request, CancellationToken ct)
    {
        request = JsonDefaults.Copy(request); // Freeze caller-owned arrays before awaiting.
        Require(request.RequestId != Guid.Empty, "invalid_request", "요청 ID가 필요합니다.", 400);
        using (_host.Open())
        {
            var session = HiperwallReaderSession(token);
            if (_host.Current.HiperwallDisplays.SingleOrDefault(j => j.Request.RequestId == request.RequestId) is { } prior)
            {
                Require(prior.Requester.UserId == session.Info.UserId && JsonSerializer.Serialize(prior.Request, JsonDefaults.Options) ==
                    JsonSerializer.Serialize(request, JsonDefaults.Options), "request_conflict", "같은 요청 ID의 내용 또는 요청자가 다릅니다.");
                return JsonDefaults.Copy(prior);
            }
            Require(!_host.Current.HiperwallEdits.Any(e => e.Request.RequestId == request.RequestId), "request_conflict", "LIVE 요청 ID가 이미 사용되었습니다.");
        }
        Require(await _hiperwallAdmission.WaitAsync(0, ct), "hiperwall_busy", "다른 표시 요청을 확인 중입니다.");
        try
        {
            HiperwallConfiguration config; HiperwallPlacement[] placements; DisplayDuration duration; string name;
            using (_host.Open())
            {
                DisplayOwner(token, request.Generation, request.ConfigurationVersion); RequireHiperwallScenarioAvailable(_host.Current); RequireNoSlotRestore(); config = _host.Current.Hiperwall!;
                Require(_host.Current.HiperwallDisplays.Count(j => j.Outstanding) < 100, "display_limit", "남은 표시 작업을 먼저 정리하세요.");
                if (request.LayoutId is { } id)
                {
                    Require(request.TestPlacements is null, "invalid_request", "저장 배치와 테스트 초안을 함께 지정할 수 없습니다.", 400);
                    var layout = _host.Current.HiperwallLayouts.SingleOrDefault(l => l.Id == id);
                    Require(layout is not null && layout.Version == request.LayoutVersion && layout.ConfigurationVersion == config.Version,
                        "layout_changed", "배치 또는 연결 설정이 변경되었습니다. 검토 후 다시 저장하세요.");
                    placements = JsonDefaults.Copy(layout!.Placements); duration = layout.Duration; name = layout.Name;
                }
                else
                {
                    Require(request.LayoutVersion is null, "invalid_request", "테스트에는 저장 배치 버전을 지정하지 않습니다.", 400);
                    ValidatePlacements(request.TestPlacements); placements = request.TestPlacements!;
                    duration = new(DisplayDurationMode.Timed, 15); name = "미저장 초안 15초 테스트";
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(config.TimeoutMs);
            var secret = config.CredentialId is { } key ? _credentials!.Read(key) : null;
            HiperwallReading reading;
            try { reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token).ConfigureAwait(false); }
            finally { secret = null; }
            RequireWritableInventory(reading);
            var commands = placements.Select((p, i) => new HiperwallWireCommand(HiperwallEditAction.Open,
                $"integrated-{request.RequestId:N}-{i}", p.Selector, p.ContentValue, p.ZoneId, p.Layout, p.Volume, p.Muted)).ToArray();
            foreach (var c in commands) ValidateDisplayContent(c, reading);
            using (_host.Open())
            {
                ct.ThrowIfCancellationRequested();
                var session = DisplayOwner(token, request.Generation, request.ConfigurationVersion); RequireHiperwallScenarioAvailable(_host.Current); RequireNoSlotRestore();
                if (request.LayoutId is { } id) Require(_host.Current.HiperwallLayouts.Any(l => l.Id == id && l.Version == request.LayoutVersion),
                    "layout_changed", "확인 중 배치가 변경되었습니다. 다시 선택하세요.");
                var next = _host.Draft();
                var job = new HiperwallDisplayJob { Request = request, Requester = session.Info, Endpoint = config.Endpoint,
                    Name = name, Duration = duration, AcceptedAt = _host.Now,
                    CloseAt = duration.EffectiveSeconds is { } seconds ? _host.Now.AddSeconds(seconds) : null,
                    Targets = commands.Select(c => new HiperwallDisplayTarget { Command = c, NextAttemptAt = _host.Now }).ToList() };
                next.HiperwallDisplays.Add(job);
                _host.Audit(next, session.Info.UserId, "HiperwallDisplayAccepted", $"request={request.RequestId}; targets={commands.Length}");
                _host.Commit(next); return JsonDefaults.Copy(job);
            }
        }
        finally { _hiperwallAdmission.Release(); }
    }
    public HiperwallDisplayJob StopHiperwallDisplay(string token, JobActionRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation);
        Require(HiperwallPermission(_host.User(s, session)), "hiperwall_scope", "전체 장비 제어 권한이 필요합니다.", 403);
        var job = s.HiperwallDisplays.SingleOrDefault(j => j.Request.RequestId == request.JobId);
        Require(job is not null, "request_not_found", "표시 작업이 없습니다.", 404);
        if (job!.ScenarioJobId is { } parentId && s.Jobs.SingleOrDefault(j => j.Id == parentId) is { Active: true } parent)
            _jobs.StopJob(s, parent.Id, session, "시나리오 표시 종료 요청");
        if (s.HiperwallEdits.SingleOrDefault(e => e.Request.RequestId == request.JobId && e.Request.Action == HiperwallEditAction.RestoreSlot) is { } restore)
            foreach (var step in restore.Steps.Where(step => step.State == HiperwallSendState.Pending))
            { step.State = HiperwallSendState.Rejected; step.Message = "표시 종료 요청으로 슬롯 불러오기 후속 전송을 중단했습니다."; }
        job.StopRequested = true; job.StoppedBy = session.Info.UserId; job.StopperName = session.Info.UserName;
        foreach (var t in job.Targets.Where(t => t.Outstanding))
        { t.CleanupAttempts = 0; t.NextAttemptAt = _host.Now; t.CleanupState = DisplayCleanupState.Tracking; }
        _host.Audit(s, session.Info.UserId, "HiperwallDisplayStop", $"request={request.JobId}; 미전송 차단·열린 표시 정리");
        return job;
    });
    private static void RequireWritableInventory(HiperwallReading r)
    {
        Require(r.State == HiperwallConnectionState.Connected && r.Controller?.Role is "Primary" or "Default" &&
            r.Instances.State == HiperwallListState.Available, "hiperwall_inventory", "Controller 연결·역할·인스턴스 목록 확인이 필요합니다.");
    }
    private static void ValidateDisplayContent(HiperwallWireCommand c, HiperwallReading r)
    {
        Require(r.Zones.State == HiperwallListState.Available && r.Zones.Items.Count(z => z.Id == c.ZoneId) == 1,
            "hiperwall_zone_missing", "배치의 Zone이 없거나 중복됩니다.");
        var matches = r.Contents.Items.Where(i => c.Selector == "uuid" ? i.Id == c.ContentValue : i.Name == c.ContentValue).ToArray();
        Require(r.Contents.State == HiperwallListState.Available && matches.Length == 1 && (c.Selector == "uuid" || matches[0].Id is null),
            "hiperwall_content_missing", "배치의 콘텐츠가 없거나 중복됩니다. UUID를 우선 사용하세요.");
        Require(r.Instances.Items.All(i => i.Id != c.InstanceId), "hiperwall_instance_exists", "표시 인스턴스 ID가 이미 존재합니다. 자동 재전송하지 않습니다.");
    }
    private static bool MatchesDisplaySource(HiperwallItem item, HiperwallWireCommand c) => c.Selector == "uuid"
        ? item.Fields.GetValueOrDefault("content.uuid") == c.ContentValue
        : item.Name == c.ContentValue && string.IsNullOrEmpty(item.Fields.GetValueOrDefault("content.uuid"));
}
