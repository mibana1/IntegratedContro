using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record HiperwallJobRow(HiperwallEditReceipt? Receipt, bool PreviousSession, bool CanCancel, HiperwallDisplayJob? Display = null)
{
    private SessionInfo Owner => Display?.Requester ?? Receipt!.Requester;
    public Guid Id => Display?.Request.RequestId ?? Receipt!.Request.RequestId;
    public string Requester => $"{Owner.UserName} / {Owner.PcName}";
    public string OriginSession => Owner.Id.ToString()[..8];
    public string AcceptedAt => (Display?.AcceptedAt ?? Receipt!.AcceptedAt).ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Name => Display?.Name ?? HiperwallEditing.ActionName(Receipt!.Request.Action);
    public string Targets => Display is { } j ? $"{j.Endpoint} · " + string.Join(", ", j.Targets.Select(t => t.Command.InstanceId)) :
        $"{Receipt!.Endpoint} · " + string.Join(", ", Receipt.Steps.Select(s => s.Command.InstanceId ?? "Hiperwall 전체").Distinct());
    public string Progress => Display is { } j ? $"{j.Summary} · {j.Schedule}" :
        $"응답 {Receipt!.Steps.Count(s => s.State == HiperwallSendState.Acknowledged)} / 대기 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Pending)} / 전송 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Sending)} / 불확실 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Unknown)}";
    public string Cancellation => Display is not null ? CanCancel ? "미전송 중단·열린 표시 종료 가능" : "사용권·전체 장비 제어 권한 필요" :
        !Receipt!.Steps.Any(s => s.State == HiperwallSendState.Pending) ? "취소할 미전송 단계 없음" : CanCancel ? "미전송 부분 선택 취소 가능" : "사용권·전체 장비 제어 권한 필요";
    public string Details => Display is { } j ?
        $"작업: {Name}\n원 요청자: {Requester}\n원 세션: {Owner.Id} | 사용권 세대: {j.Request.Generation}\n요청: {Id}\n{Targets}\n{Progress}\n종료 요청자: {j.StopperName ?? "-"}\n" +
            string.Join("\n", j.Targets.Select(t => $"{t.Command.InstanceId} · {t.Command.ContentValue} · {t.Command.ZoneId}\n{t.Message}")) :
        $"작업: {Name}\n원 요청자: {Requester}\n원 세션: {Owner.Id} | 사용권 세대: {Receipt!.Request.Generation}\n" +
        $"접수: {Receipt.AcceptedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | 요청 ID: {Id}\nController: {Receipt.Endpoint} | 설정 v{Receipt.Request.ConfigurationVersion}\n{Receipt.Summary}\n{Cancellation}\n\n" +
        string.Join("\n", Receipt.Steps.Select((s, i) => $"{i + 1}. {s.Command.InstanceId ?? "Hiperwall 전체"} | 콘텐츠: {s.Command.ContentValue ?? "-"} | Zone: {s.Command.ZoneId ?? "-"} | {s.State} · {s.Message}"));
}

public sealed partial class MainViewModel
{
    public ObservableCollection<HiperwallJobRow> HiperwallJobs { get; } = [];
    private HiperwallJobRow? _selectedHiperwallJob;
    public HiperwallJobRow? SelectedHiperwallJob
    {
        get => _selectedHiperwallJob;
        set
        {
            Set(ref _selectedHiperwallJob, value); Changed(nameof(HiperwallJobDetails)); Changed(nameof(HiperwallCancelLabel));
            CancelHiperwallJobCommand.Raise();
        }
    }
    public string HiperwallCancelLabel => SelectedHiperwallJob?.Display is null ? "선택 Hiperwall 미전송 부분 취소" : "선택 표시 종료 · 정리 재시도";
    public string HiperwallJobsSummary => $"Hiperwall 진행·표시·정리·결과 확인 필요 {HiperwallJobs.Count}건";
    public string HiperwallJobDetails => SelectedHiperwallJob?.Details ?? "Hiperwall 작업을 선택하면 원 요청자·대상·결과와 취소·정리 가능 범위를 확인할 수 있습니다.";
    public AsyncCommand CancelHiperwallJobCommand { get; private set; } = null!;
    private void InitializeHandover()
    {
        CancelHiperwallJobCommand = Command(async () =>
        {
            if (SelectedHiperwallJob!.Display is not null)
            {
                await Client.Post<HiperwallDisplayJob>("/api/hiperwall/displays/stop", new JobActionRequest(Generation, SelectedHiperwallJob.Id));
                Message = "선택 표시의 미전송 부분 중단과 열린 인스턴스 정리를 요청했습니다.";
            }
            else
            {
                var result = await Client.Post<HiperwallEditReceipt>("/api/hiperwall/edits/cancel", new JobActionRequest(Generation, SelectedHiperwallJob.Id));
                Message = $"Hiperwall 미전송 부분 취소 처리: {result.Summary}. 이미 전송한 명령은 결과 확인이 필요합니다.";
            }
        }, () => CanControl && _state?.CanControlHiperwall == true && SelectedHiperwallJob?.CanCancel == true);
    }
    private void RefreshHandover()
    {
        var selectedId = _selectedHiperwallJob?.Id;
        var displays = _state?.OutstandingHiperwallDisplays ?? [];
        var tracked = displays.Select(j => j.Request.RequestId).ToHashSet();
        Replace(HiperwallJobs, (_state?.OutstandingHiperwallEdits ?? []).Where(r => r.NeedsAttention && !tracked.Contains(r.Request.RequestId))
            .Select(r => new HiperwallJobRow(r, r.Requester.Id != _login?.Session.Id,
                CanControl && _state?.CanControlHiperwall == true && r.Steps.Any(s => s.State == HiperwallSendState.Pending)))
            .Concat(displays.Select(j => new HiperwallJobRow(null, j.Requester.Id != _login?.Session.Id, CanControl && _state?.CanControlHiperwall == true, j))));
        _selectedHiperwallJob = HiperwallJobs.FirstOrDefault(r => r.Id == selectedId);
        Changed(nameof(SelectedHiperwallJob)); Changed(nameof(HiperwallJobDetails)); Changed(nameof(HiperwallJobsSummary)); Changed(nameof(HiperwallCancelLabel));
    }
}
