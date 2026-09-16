using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private static bool DisplayDue(HiperwallDisplayJob j, DateTimeOffset now) => j.StopRequested || j.CloseAt <= now;
    private static void TrackLiveOpen(HostState s, HiperwallEditReceipt edit, DateTimeOffset now)
    {
        if (edit.Request.Action is not (HiperwallEditAction.Open or HiperwallEditAction.RestoreSlot)) return;
        foreach (var step in edit.Steps.Where(t => t.Command.Action == HiperwallEditAction.Open &&
            t.State is HiperwallSendState.Sending or HiperwallSendState.Acknowledged or HiperwallSendState.Unknown))
        {
            var job = s.HiperwallDisplays.SingleOrDefault(j => j.Request.RequestId == edit.Request.RequestId);
            if (job is null)
            {
                job = new HiperwallDisplayJob {
                    Request = new(edit.Request.RequestId, edit.Request.Generation, edit.Request.ConfigurationVersion),
                    Requester = edit.Requester, Endpoint = edit.Endpoint,
                    Name = edit.SlotSnapshot is { } slot ? $"저장 슬롯 {slot.Number} 불러오기" : "LIVE 추가 · " + step.Command.ContentValue,
                    Duration = new(DisplayDurationMode.Continuous), AcceptedAt = edit.AcceptedAt
                };
                s.HiperwallDisplays.Add(job);
            }
            if (job.Targets.All(t => t.Command.InstanceId != step.Command.InstanceId))
                job.Targets.Add(new() { Command = step.Command, OpenState = step.State, OpenAttempted = true, NextAttemptAt = now });
        }
    }
    private void RecoverHiperwallDisplays(HostState next)
    {
        foreach (var edit in next.HiperwallEdits.Where(e => e.Request.Action is (HiperwallEditAction.Open or HiperwallEditAction.RestoreSlot) &&
            e.Steps.Any(t => t.State is HiperwallSendState.Sending or HiperwallSendState.Acknowledged or HiperwallSendState.Unknown)))
            TrackLiveOpen(next, edit, Now);
        foreach (var job in next.HiperwallDisplays.Where(j => j.Outstanding))
            foreach (var t in job.Targets.Where(t => t.Outstanding))
            {
                if (t.OpenState == HiperwallSendState.Sending) t.OpenState = HiperwallSendState.Unknown;
                // Never replay an interrupted open, including a never-sent part of a batch.
                if (t.OpenState == HiperwallSendState.Pending)
                { t.OpenState = HiperwallSendState.Rejected; t.CleanupState = DisplayCleanupState.Closed; t.Message = "호스트 재시작: 미전송 표시 중단"; }
                else
                { if (t.CleanupState == DisplayCleanupState.Closing) t.CleanupState = DisplayCleanupState.Tracking; t.NextAttemptAt = Now; }
            }
    }
    private void MarkDisplaysForShutdown()
    {
        if (_storageFailed) return;
        var next = JsonDefaults.Copy(_state);
        foreach (var j in next.HiperwallDisplays.Where(j => j.Outstanding))
        {
            j.StopRequested = true;
            foreach (var t in j.Targets.Where(t => t.Outstanding)) t.NextAttemptAt = Now;
        }
        Persist(next); // Preserve cleanup intent even if the shutdown deadline expires.
    }
    public async Task CleanupHiperwallOnShutdownAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool pending;
            lock (_gate) pending = !_storageFailed && _hiperwall is IHiperwallWriter && _credentials is not null && _state.HiperwallDisplays.Any(j => j.Targets.Any(t =>
                t.Outstanding && t.CleanupState != DisplayCleanupState.NeedsReview && t.NextAttemptAt <= Now));
            if (!pending) return;
            await ReconcileHiperwallDisplaysAsync(ct, shutdown: true).ConfigureAwait(false);
        }
    }
    public async Task ReconcileHiperwallDisplaysAsync(CancellationToken ct, bool shutdown = false)
    {
        if (!await _hiperwallWrites.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            HiperwallDisplayJob snapshot; int index; HiperwallConfiguration config;
            lock (_gate)
            {
                if (_storageFailed || (_stopping && !shutdown) || ct.IsCancellationRequested || _hiperwall is not IHiperwallWriter || _credentials is null) return;
                // Keep cleanup from removing the source view during an accepted replacement.
                if (!shutdown && _state.HiperwallEdits.Any(e => e.Active && e.Request.Action == HiperwallEditAction.RestoreSlot)) return;
                var job = _state.HiperwallDisplays.Where(j => j.Targets.Any(t => t.Outstanding &&
                        t.CleanupState != DisplayCleanupState.NeedsReview && (t.NextAttemptAt <= Now || DisplayDue(j, Now) && t.CleanupAttempts == 0)))
                    .OrderByDescending(j => DisplayDue(j, Now)).ThenBy(j => j.AcceptedAt).FirstOrDefault();
                if (job is null) return;
                index = job.Targets.FindIndex(t => t.Outstanding && t.CleanupState != DisplayCleanupState.NeedsReview &&
                    (t.NextAttemptAt <= Now || DisplayDue(job, Now) && t.CleanupAttempts == 0));
                snapshot = JsonDefaults.Copy(job);
                var target = job.Targets[index];
                if (!target.OpenAttempted && (DisplayDue(job, Now) || !CanOpenDisplay(job)))
                {
                    UpdateDisplay(job.Request.RequestId, index, t => {
                        t.OpenState = HiperwallSendState.Rejected; t.CleanupState = DisplayCleanupState.Closed;
                        t.Message = "미전송 표시 중단: 만료·취소·계정 권한·연결 설정 확인";
                    });
                    return;
                }
                if (_state.Hiperwall is not { } current || current.Endpoint != job.Endpoint)
                {
                    UpdateDisplay(job.Request.RequestId, index, t => { t.CleanupState = DisplayCleanupState.NeedsReview;
                        t.Message = "원 Controller 연결을 복구한 뒤 표시 종료·정리를 다시 요청하세요."; });
                    return;
                }
                config = current;
            }
            var id = snapshot.Request.RequestId;
            var command = snapshot.Targets[index].Command;
            var attempted = snapshot.Targets[index].OpenAttempted;
            var sent = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(config.TimeoutMs);
                var secret = config.CredentialId is { } reference ? _credentials!.Read(reference) : null;
                try
                {
                    var reading = await _hiperwall!.ReadAsync(config, secret, timeout.Token).ConfigureAwait(false);
                    RequireWritableInventory(reading);
                    var observed = reading.Instances.Items.SingleOrDefault(i => i.Id == command.InstanceId);
                    if (attempted)
                    {
                        if (observed is null)
                        {
                            lock (_gate) UpdateDisplay(id, index, t => { t.CleanupState = DisplayCleanupState.Closed;
                                t.Message = "Controller 목록에서 인스턴스 없음 확인"; });
                            return;
                        }
                        Require(MatchesDisplaySource(observed, command), "display_identity", "같은 ID의 콘텐츠가 변경되었습니다. 자동 정리를 차단했습니다.");
                        Task<HiperwallWriteResult>? closing = null;
                        lock (_gate)
                        {
                            if (_storageFailed || _state.Hiperwall?.Version != config.Version || (_stopping && !shutdown)) return;
                            var current = _state.HiperwallDisplays.Single(j => j.Request.RequestId == id);
                            if (DisplayDue(current, Now))
                            {
                                ct.ThrowIfCancellationRequested(); timeout.Token.ThrowIfCancellationRequested();
                                UpdateDisplay(id, index, t => { t.CleanupState = DisplayCleanupState.Closing; t.CleanupAttempts++;
                                    t.Message = "닫기 전송 의도 저장"; });
                                sent = true;
                                closing = ((IHiperwallWriter)_hiperwall).WriteAsync(config, secret,
                                    new(HiperwallEditAction.Close, command.InstanceId), timeout.Token);
                            }
                            else UpdateDisplay(id, index, t => { t.OpenState = HiperwallSendState.Acknowledged;
                                t.Message = "Controller 목록에서 표시 인스턴스 확인"; t.NextAttemptAt = Now.AddSeconds(5); });
                        }
                        if (closing is null) return;
                        var result = await closing.ConfigureAwait(false);
                        // ACK, 404 and lost responses all require absence in a fresh inventory.
                        using var verify = CancellationTokenSource.CreateLinkedTokenSource(ct); verify.CancelAfter(config.TimeoutMs);
                        var after = await _hiperwall.ReadAsync(config, secret, verify.Token).ConfigureAwait(false);
                        RequireWritableInventory(after);
                        var absent = after.Instances.Items.All(i => i.Id != command.InstanceId);
                        lock (_gate)
                        {
                            if (absent) UpdateDisplay(id, index, t => { t.CleanupState = DisplayCleanupState.Closed; t.Message = "닫기 후 목록에서 없음 확인"; });
                            else ScheduleCleanupRetry(id, index, result.State == HiperwallSendState.Rejected,
                                "닫기 후에도 인스턴스가 남아 있습니다. " + result.Message);
                        }
                    }
                    else
                    {
                        ValidateDisplayContent(command, reading);
                        Task<HiperwallWriteResult> opening;
                        lock (_gate)
                        {
                            var current = _state.HiperwallDisplays.Single(j => j.Request.RequestId == id);
                            if (!CanOpenDisplay(current) || DisplayDue(current, Now)) return;
                            ct.ThrowIfCancellationRequested(); timeout.Token.ThrowIfCancellationRequested();
                            UpdateDisplay(id, index, t => { t.OpenState = HiperwallSendState.Sending; t.OpenAttempted = true; t.Message = "열기 전송 의도 저장"; });
                            sent = true;
                            opening = ((IHiperwallWriter)_hiperwall!).WriteAsync(config, secret, command, timeout.Token);
                        }
                        var result = await opening.ConfigureAwait(false);
                        lock (_gate) UpdateDisplay(id, index, t => { t.OpenState = result.State; t.Message = result.Message;
                            t.NextAttemptAt = Now; }); // Even rejected replies are reconciled; never replay open.
                    }
                }
                finally { secret = null; }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                lock (_gate)
                {
                    if (_storageFailed) return;
                    if (!attempted && !sent)
                    {
                        UpdateDisplay(id, index, t => { t.OpenState = HiperwallSendState.Rejected; t.CleanupState = DisplayCleanupState.Closed;
                            t.Message = e is DomainException d ? d.Message : "표시 전 확인 실패: 명령을 보내지 않았습니다."; });
                    }
                    else if (!attempted)
                        UpdateDisplay(id, index, t => { t.OpenState = HiperwallSendState.Unknown; t.NextAttemptAt = Now;
                            t.Message = "열기 응답 확인 필요 · 자동 재전송 없음"; });
                    else ScheduleCleanupRetry(id, index, e is DomainException { Code: "display_identity" },
                        e is DomainException error ? error.Message : "목록 대조·정리 실패. 기록을 유지하고 다시 확인합니다.");
                }
            }
        }
        finally { _hiperwallWrites.Release(); }
    }
    private bool CanOpenDisplay(HiperwallDisplayJob j) => !_stopping && !_storageFailed && ScenarioAllowsDisplay(j) &&
        _state.Hiperwall?.Version == j.Request.ConfigurationVersion &&
        _state.Accounts.Any(a => a.Id == j.Requester.UserId && HiperwallPermission(a));
    private void UpdateDisplay(Guid id, int index, Action<HiperwallDisplayTarget> update)
    {
        var next = JsonDefaults.Copy(_state);
        var job = next.HiperwallDisplays.Single(j => j.Request.RequestId == id);
        var target = job.Targets[index]; var wasClosed = !target.Outstanding;
        update(target);
        if (!wasClosed && !target.Outstanding || target.CleanupState == DisplayCleanupState.NeedsReview)
            Audit(next, job.Requester.UserId, "HiperwallDisplayCleanup", $"request={id}; target={index}; state={target.CleanupState}");
        // Latch the scenario outcome in the same transaction as the first open result.
        // Inventory reconciliation may later resolve an unknown open, but must not resume its parent.
        if (job.ScenarioJobId is { } parentId && job.ScenarioStepIndex is { } stepIndex &&
            next.Jobs.SingleOrDefault(j => j.Id == parentId) is { Active: true } parent &&
            parent.Steps[stepIndex].Status == StepStatus.Waiting &&
            target.OpenState is HiperwallSendState.Unknown or HiperwallSendState.Rejected)
        {
            var invalid = Revalidate(next, parent, parent.Snapshot.Steps[stepIndex]);
            var status = target.OpenState == HiperwallSendState.Unknown ? StepStatus.Unknown :
                invalid is not null ? StepStatus.Skipped : Now >= parent.Steps[stepIndex].DeadlineAt ? StepStatus.Failed :
                !target.OpenAttempted ? StepStatus.Skipped : StepStatus.Failed;
            FinishStep(next, parent, stepIndex, new(status, invalid ?? target.Message));
        }
        // Reconciled LIVE opens no longer remain unknown in handover history.
        if (next.HiperwallEdits.SingleOrDefault(e => e.Request.RequestId == id) is { } edit &&
            edit.Steps[0].State == HiperwallSendState.Unknown && (target.OpenState == HiperwallSendState.Acknowledged || !target.Outstanding))
        { edit.Steps[0].State = target.OpenState == HiperwallSendState.Acknowledged ? HiperwallSendState.Acknowledged : HiperwallSendState.Rejected;
          edit.Steps[0].Message = target.Message; }
        Persist(next);
    }
    private void ScheduleCleanupRetry(Guid id, int index, bool permanent, string message) => UpdateDisplay(id, index, t =>
    {
        if (t.CleanupState != DisplayCleanupState.Closing) t.CleanupAttempts++;
        t.CleanupState = permanent || t.CleanupAttempts >= 8 ? DisplayCleanupState.NeedsReview : DisplayCleanupState.Tracking;
        t.NextAttemptAt = Now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(t.CleanupAttempts - 1, 6))));
        t.Message = message + (t.CleanupState == DisplayCleanupState.NeedsReview ? " 원인 확인 후 표시 종료·정리로 재시도하세요." : "");
    });
}
