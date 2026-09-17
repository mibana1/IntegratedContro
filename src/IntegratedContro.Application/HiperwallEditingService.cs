using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class HiperwallService
{
    private readonly SemaphoreSlim _hiperwallAdmission = new(1, 1);
    private int _hiperwallDispatching;
    public HiperwallEditReceipt[] GetHiperwallEdits(string token)
    {
        using (_host.Open())
        {
            HiperwallReaderSession(token);
            var recent = _host.Current.HiperwallEdits.TakeLast(100).Select(r => r.Request.RequestId).ToHashSet();
            return JsonDefaults.Copy(_host.Current.HiperwallEdits.Where(r => r.NeedsAttention || recent.Contains(r.Request.RequestId))
                .Reverse().ToArray());
        }
    }
    public HiperwallEditReceipt GetHiperwallEdit(string token, Guid id)
    {
        using (_host.Open())
        {
            HiperwallReaderSession(token);
            var receipt = _host.Current.HiperwallEdits.SingleOrDefault(r => r.Request.RequestId == id);
            Require(receipt is not null, "request_not_found", "접수 기록이 없습니다. 목록과 전송 이력을 확인하세요.", 404);
            return JsonDefaults.Copy(receipt!);
        }
    }
    private Session HiperwallOwner(string token, HiperwallEditRequest request)
    {
        _host.Healthy(); _host.CheckConnections();
        var session = _host.Owner(_host.Current, token, request.Generation);
        Require(HiperwallPermission(_host.User(_host.Current, session)), "hiperwall_scope", "Hiperwall 제어에는 전체 장비 제어 권한이 필요합니다.", 403);
        Require(_hiperwall is IHiperwallWriter && _credentials is not null, "hiperwall_unavailable", "Hiperwall 편집을 지원하는 호스트가 필요합니다.", 503);
        Require(_host.Current.Hiperwall is not null && _host.Current.Hiperwall.Version == request.ConfigurationVersion,
            "hiperwall_settings_changed", "연결 설정이 변경되었습니다. 다시 조회하세요.");
        return session;
    }
    public async Task<HiperwallEditReceipt> EditHiperwallAsync(string token, HiperwallEditRequest request, CancellationToken ct)
    {
        Require(request.RequestId != Guid.Empty && Enum.IsDefined(request.Action), "invalid_request", "유효한 요청 ID와 동작이 필요합니다.", 400);
        Require(request.Action == HiperwallEditAction.RestoreSlot || request.SlotNumber is null && request.SlotVersion is null,
            "invalid_request", "일반 편집 명령에는 저장 슬롯을 지정할 수 없습니다.", 400);
        using (_host.Open())
        {
            var session = HiperwallReaderSession(token);
            if (_host.Current.HiperwallEdits.SingleOrDefault(r => r.Request.RequestId == request.RequestId) is { } existing)
            {
                Require(existing.Requester.UserId == session.Info.UserId && existing.Request == request,
                    "request_conflict", "같은 요청 ID의 내용 또는 요청자가 다릅니다.");
                return JsonDefaults.Copy(existing);
            }
        }
        using (_host.Open())
            Require(!_host.Current.HiperwallDisplays.Any(j => j.Request.RequestId == request.RequestId), "request_conflict", "표시 작업 ID가 이미 사용되었습니다.");
        Require(await _hiperwallAdmission.WaitAsync(0, ct), "hiperwall_busy", "다른 Hiperwall 요청을 확인 중입니다. 처리 후 다시 조작하세요.");
        var ownsWriteLane = false;
        try
        {
            if (request.Action == HiperwallEditAction.RestoreSlot)
            {
                ownsWriteLane = await _hiperwallWrites.WaitAsync(0, ct).ConfigureAwait(false);
                Require(ownsWriteLane, "hiperwall_busy", "현재 전송을 처리 중입니다. 완료 후 불러오세요.");
            }
            HiperwallConfiguration config; HiperwallSlot? slot = null;
            using (_host.Open())
            {
                HiperwallOwner(token, request); RequireHiperwallScenarioAvailable(_host.Current);
                Require(!_host.Current.HiperwallEdits.Any(r => r.Active), "hiperwall_busy", "접수된 Hiperwall 명령을 처리 중입니다.");
                config = _host.Current.Hiperwall!;
                if (request.Action == HiperwallEditAction.RestoreSlot)
                {
                    Require(!_host.Current.HiperwallDisplays.Any(j => j.Targets.Any(t => t.OpenState is HiperwallSendState.Pending or HiperwallSendState.Sending)),
                        "hiperwall_busy", "접수된 표시가 진행 중입니다. 완료 후 불러오세요.");
                    slot = ReadRestoreSlot(request);
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(config.TimeoutMs);
            var secret = config.CredentialId is { } id ? _credentials!.Read(id) : null;
            HiperwallReading reading;
            try { reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token).ConfigureAwait(false); }
            finally { secret = null; }
            Require(reading.State == HiperwallConnectionState.Connected && reading.Controller?.Role is "Primary" or "Default",
                "hiperwall_not_writable", "Controller 연결·인증·역할을 확인하세요. Shadow Controller에는 편집 명령을 보내지 않습니다.");
            var commands = slot is null ? ValidateHiperwallEdit(request, reading) : BuildSlotRestore(request, slot, reading);
            using (_host.Open())
            {
                ct.ThrowIfCancellationRequested();
                var session = HiperwallOwner(token, request); RequireHiperwallScenarioAvailable(_host.Current);
                if (slot is not null) ReadRestoreSlot(request);
                var next = _host.Draft();
                var receipt = new HiperwallEditReceipt { Request = request, Requester = session.Info, Endpoint = config.Endpoint,
                    AcceptedAt = _host.Now, SlotSnapshot = slot, Steps = commands.Select(c => new HiperwallEditStep { Command = c,
                        ExpectedTargetRevision = c.Action is HiperwallEditAction.Close or HiperwallEditAction.Change
                            ? HiperwallEditing.Revision(reading.Instances.Items.Where(i => i.Id == c.InstanceId)) : null }).ToList() };
                next.HiperwallEdits.Add(receipt);
                _host.Audit(next, session.Info.UserId, "HiperwallEditAccepted", $"{HiperwallEditing.ActionName(request.Action)}; request={request.RequestId}; targets={commands.Length}");
                _host.Commit(next);
                _hiperwallSequence++;
                _hiperwallView = EmptyHiperwall(message: "편집 명령을 접수했습니다. 전송 결과와 새 목록을 확인하세요.");
                return JsonDefaults.Copy(receipt);
            }
        }
        finally { if (ownsWriteLane) _hiperwallWrites.Release(); _hiperwallAdmission.Release(); }
    }
    private static HiperwallWireCommand[] ValidateHiperwallEdit(HiperwallEditRequest r, HiperwallReading state)
    {
        Require(r.Layout is not { IsValid: false } && r.Volume is not (< 0 or > 100), "invalid_layout", "유한한 좌표·양수 크기와 0~100 정수 음량이 필요합니다.", 400);
        Require(state.Instances.State == HiperwallListState.Available, "hiperwall_inventory", "열린 콘텐츠 목록을 확인할 수 없습니다.");
        if (r.Action == HiperwallEditAction.Open || r.Layout is not null)
        {
            Text(r.ZoneId, "Zone ID", 4096);
            Require(state.Zones.State == HiperwallListState.Available && state.Zones.Items.Count(z => z.Id == r.ZoneId) == 1,
                "hiperwall_zone_missing", "선택 Zone이 없어졌거나 식별자가 중복됩니다. 다시 조회하세요.");
        }
        HiperwallItem? target = null;
        if (r.Action is HiperwallEditAction.Change or HiperwallEditAction.Close)
        {
            Text(r.InstanceId, "인스턴스 ID", 256);
            target = state.Instances.Items.SingleOrDefault(i => i.Id == r.InstanceId);
            Require(target is not null, "hiperwall_instance_missing", "선택 인스턴스가 없어졌습니다. 다시 조회하세요.");
            Require(r.ExpectedRevision == HiperwallEditing.Revision([target!]), "hiperwall_revision", "선택 인스턴스가 외부에서 변경되었습니다. 새로 고침 후 다시 조작하세요.");
        }
        if (r.Action is HiperwallEditAction.CloseAll or HiperwallEditAction.MuteAll)
        {
            Require(r.ExpectedRevision == HiperwallEditing.Revision(state.Instances.Items), "hiperwall_revision", "열린 콘텐츠 집합이 변경되었습니다. 새로 고침 후 대상을 다시 확인하세요.");
            Require(r.InstanceId is null && r.Layout is null && r.Selector is null && r.ContentValue is null && r.ZoneId is null && r.Volume is null,
                "invalid_request", "전체 제어에 불필요한 대상 값이 있습니다.", 400);
        }
        if (r.Action == HiperwallEditAction.Open)
        {
            Require(r.Selector is "uuid" or "name", "invalid_selector", "UUID 또는 원문 전체 이름이 필요합니다.", 400);
            Text(r.ContentValue, "콘텐츠 식별자", 4096);
            var contents = state.Contents.Items.Where(i => r.Selector == "uuid" ? i.Id == r.ContentValue : i.Name == r.ContentValue).ToArray();
            Require(state.Contents.State == HiperwallListState.Available && contents.Length == 1 && (r.Selector == "uuid" || contents[0].Id is null),
                "hiperwall_content_missing", "콘텐츠가 없거나 이름이 중복됩니다. UUID를 우선 사용하고 목록을 다시 조회하세요.");
            Require(state.Instances.Items.All(i => i.Id != "integrated-" + r.RequestId.ToString("N")), "hiperwall_instance_exists", "추가할 인스턴스 ID가 이미 존재합니다. 목록을 대조하세요.");
            Require(r.Layout is not null && r.InstanceId is null, "invalid_layout", "새 콘텐츠의 위치·크기가 필요합니다.", 400);
            return [new(r.Action, "integrated-" + r.RequestId.ToString("N"), r.Selector, r.ContentValue, r.ZoneId, r.Layout, r.Volume, r.Muted)];
        }
        Require(r.Selector is null && r.ContentValue is null, "invalid_request", "인스턴스 명령에는 콘텐츠 선택자를 사용하지 않습니다.", 400);
        if (r.Action == HiperwallEditAction.Change)
        {
            Require(r.Layout is not null || r.Volume is not null || r.Muted is not null, "invalid_request", "변경할 값을 입력하세요.", 400);
            if (r.Layout is not null) Require(HiperwallGeometry.TryInstance(target!, out _, out _), "hiperwall_geometry", "현재 인스턴스의 위치·크기·회전 정보를 확인할 수 없습니다.");
            if (r.Volume is not null || r.Muted is not null) Require(HiperwallEditing.TryAudio(target!, out _, out _), "hiperwall_audio", "선택 인스턴스의 소리 지원 정보를 확인할 수 없습니다.");
            return [new(r.Action, r.InstanceId, ZoneId: r.ZoneId, Layout: r.Layout, Volume: r.Volume, Muted: r.Muted)];
        }
        if (r.Action == HiperwallEditAction.Close)
        {
            Require(r.Layout is null && r.Volume is null && r.Muted is null && r.ZoneId is null, "invalid_request", "닫기에는 인스턴스 ID만 지정하세요.", 400);
            return [new(r.Action, r.InstanceId)];
        }
        if (r.Action == HiperwallEditAction.CloseAll)
        {
            Require(r.Muted is null && state.Instances.Items.Length is > 0 and <= 1000, "invalid_request", "전체 닫기는 1~1000개 인스턴스를 지원합니다.", 400);
            return state.Instances.Items.Select(i => new HiperwallWireCommand(HiperwallEditAction.Close, i.Id)).ToArray();
        }
        Require(r.Muted is not null, "invalid_request", "전체 음소거 값을 지정하세요.", 400);
        return [new(r.Action, Muted: r.Muted)];
    }
    public HiperwallEditReceipt CancelHiperwallEdit(string token, JobActionRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation);
        Require(HiperwallPermission(_host.User(s, session)), "hiperwall_scope", "전체 장비 제어 권한이 필요합니다.", 403);
        var receipt = s.HiperwallEdits.SingleOrDefault(r => r.Request.RequestId == request.JobId);
        Require(receipt is not null, "request_not_found", "접수 기록이 없습니다.", 404);
        foreach (var step in receipt!.Steps.Where(s => s.State == HiperwallSendState.Pending))
        { step.State = HiperwallSendState.Rejected; step.Message = "현재 사용권자가 미전송 부분을 취소했습니다."; }
        _host.Audit(s, session.Info.UserId, "HiperwallEditCancelled", $"request={request.JobId}; 이미 전송한 명령은 유지");
        return receipt;
    });
    public async Task DispatchHiperwallNextAsync(CancellationToken stopping)
    {
        if (Interlocked.CompareExchange(ref _hiperwallDispatching, 1, 0) != 0) return;
        if (!await _hiperwallWrites.WaitAsync(0, stopping).ConfigureAwait(false)) { Interlocked.Exchange(ref _hiperwallDispatching, 0); return; }
        try
        {
            Guid id; int index; HiperwallEditStep snapshot; HiperwallEditReceipt receiptSnapshot; HiperwallConfiguration config; HiperwallEditRequest request;
            using (_host.Open())
            {
                if (_host.Stopping || _host.StorageFailed || stopping.IsCancellationRequested) return;
                var receipt = _host.Current.HiperwallEdits.FirstOrDefault(r => r.Steps.Any(s => s.State == HiperwallSendState.Pending));
                if (receipt is null) return;
                id = receipt.Request.RequestId; index = receipt.Steps.FindIndex(s => s.State == HiperwallSendState.Pending);
                if (!ValidateHiperwallDispatch(receipt)) { RejectHiperwallPending(id); return; }
                receiptSnapshot = JsonDefaults.Copy(receipt);
                snapshot = receiptSnapshot.Steps[index]; config = _host.Current.Hiperwall!; request = receipt.Request;
            }
            HiperwallWriteResult result;
            var sent = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(config.TimeoutMs);
                var secret = config.CredentialId is { } reference ? _credentials!.Read(reference) : null;
                try
                {
                    var reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token).ConfigureAwait(false);
                    if (request.Action == HiperwallEditAction.RestoreSlot) ValidateSlotRestoreStep(receiptSnapshot, index, reading);
                    else if (request.Action != HiperwallEditAction.CloseAll) ValidateHiperwallEdit(request, reading);
                    var command = snapshot.Command;
                    var target = reading.Instances.Items.SingleOrDefault(i => i.Id == command.InstanceId);
                    var targetValid = snapshot.ExpectedTargetRevision is null || target is not null &&
                        snapshot.ExpectedTargetRevision == HiperwallEditing.Revision([target]);
                    if (reading.State != HiperwallConnectionState.Connected || reading.Controller?.Role is not ("Primary" or "Default") ||
                        reading.Instances.State != HiperwallListState.Available || !targetValid)
                        result = new(HiperwallSendState.Rejected, "전송 직전 Controller 연결·역할 또는 대상 변경을 확인해 전송하지 않았습니다.");
                    else
                    {
                        Task<HiperwallWriteResult> sending;
                        using (_host.Open())
                        {
                            var current = _host.Current.HiperwallEdits.Single(r => r.Request.RequestId == id);
                            if (current.Steps[index].State != HiperwallSendState.Pending) return;
                            if (!ValidateHiperwallDispatch(current)) { RejectHiperwallPending(id); return; }
                            stopping.ThrowIfCancellationRequested(); timeout.Token.ThrowIfCancellationRequested();
                            var next = _host.Draft(); var step = next.HiperwallEdits.Single(r => r.Request.RequestId == id).Steps[index];
                            step.State = HiperwallSendState.Sending; step.Message = "전송 중";
                            TrackLiveOpen(next, next.HiperwallEdits.Single(r => r.Request.RequestId == id), _host.Now);
                            _host.Commit(next);
                            // The adapter initiates its HTTP send here, at the same authority boundary as permission/config validation.
                            sent = true; sending = ((IHiperwallWriter)_hiperwall!).WriteAsync(config, secret, command, timeout.Token);
                        }
                        result = await sending.ConfigureAwait(false);
                        if (command.Action == HiperwallEditAction.Close && (request.Action == HiperwallEditAction.RestoreSlot || result.State != HiperwallSendState.Acknowledged))
                        {
                            using var reconcile = CancellationTokenSource.CreateLinkedTokenSource(stopping); reconcile.CancelAfter(config.TimeoutMs);
                            var observed = await _hiperwall.ReadAsync(config, secret, reconcile.Token).ConfigureAwait(false);
                            if (observed.State == HiperwallConnectionState.Connected && observed.Instances.State == HiperwallListState.Available &&
                                observed.Instances.Items.All(i => i.Id != command.InstanceId))
                                result = new(HiperwallSendState.Acknowledged, "재조회로 인스턴스 없음 확인 · 닫기 명령은 재전송하지 않았습니다.");
                            else if (request.Action == HiperwallEditAction.RestoreSlot)
                                result = new(HiperwallSendState.Unknown, "닫기 이후 콘텐츠가 사라졌는지 확인하지 못해 불러오기를 중단했습니다.");
                        }
                    }
                }
                finally { secret = null; }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            { result = new(sent ? HiperwallSendState.Unknown : HiperwallSendState.Rejected,
                sent ? "전송 결과 확인 필요 · 자동 재전송하지 않습니다." : "전송 전 확인에 실패하여 명령을 보내지 않았습니다."); }
            using (_host.Open())
            {
                var next = _host.Draft();
                var receipt = next.HiperwallEdits.Single(r => r.Request.RequestId == id);
                var step = receipt.Steps[index];
                if (step.State == HiperwallSendState.Rejected) return; // A cancel during the read preflight wins over its late result.
                step.State = result.State; step.Message = result.Message;
                if (step.Command.Action == HiperwallEditAction.Open &&
                    next.HiperwallDisplays.SingleOrDefault(j => j.Request.RequestId == id)?.Targets
                        .SingleOrDefault(t => t.Command.InstanceId == step.Command.InstanceId) is { } tracked)
                { tracked.OpenState = result.State; tracked.Message = result.Message; }
                if (request.Action == HiperwallEditAction.RestoreSlot && result.State != HiperwallSendState.Acknowledged)
                    foreach (var pending in receipt.Steps.Where(s => s.State == HiperwallSendState.Pending))
                    { pending.State = HiperwallSendState.Rejected; pending.Message = "앞 단계 결과를 확인할 수 없어 불러오기 후속 전송을 중단했습니다."; }
                _host.Audit(next, receipt.Requester.UserId, "HiperwallEditResult", $"request={id}; {HiperwallEditing.ActionName(step.Command.Action)}; result={result.State}");
                _host.Commit(next); _hiperwallSequence++;
                _hiperwallView = EmptyHiperwall(message: "편집 전송이 처리되었습니다. 목록을 새로 고쳐 실제 상태를 확인하세요.");
            }
        }
        finally { _hiperwallWrites.Release(); Interlocked.Exchange(ref _hiperwallDispatching, 0); }
    }
    private bool ValidateHiperwallDispatch(HiperwallEditReceipt receipt) => !_host.Stopping && !_host.StorageFailed &&
        _hiperwall is IHiperwallWriter && (_host.FindAccount(_host.Current, receipt.Requester.UserId) is { } user && HiperwallPermission(user)) &&
        _host.Current.Hiperwall?.Version == receipt.Request.ConfigurationVersion;
    private void RejectHiperwallPending(Guid id)
    {
        var next = _host.Draft();
        foreach (var step in next.HiperwallEdits.Single(r => r.Request.RequestId == id).Steps.Where(s => s.State == HiperwallSendState.Pending))
        { step.State = HiperwallSendState.Rejected; step.Message = "계정 권한·연결 설정·호스트 상태가 변경되어 전송하지 않았습니다."; }
        _host.Commit(next);
    }
}
