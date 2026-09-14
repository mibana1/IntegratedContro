using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record HiperwallJobRow(HiperwallEditReceipt Receipt, bool PreviousSession, bool CanCancel)
{
    public Guid Id => Receipt.Request.RequestId;
    public string Requester => $"{Receipt.Requester.UserName} / {Receipt.Requester.PcName}";
    public string OriginSession => Receipt.Requester.Id.ToString()[..8];
    public string AcceptedAt => Receipt.AcceptedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Name => HiperwallEditing.ActionName(Receipt.Request.Action);
    public string Targets => $"{Receipt.Endpoint} · " + string.Join(", ", Receipt.Steps
        .Select(s => s.Command.InstanceId ?? "Hiperwall 전체").Distinct());
    public string Progress => $"응답 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Acknowledged)} / " +
        $"대기 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Pending)} / " +
        $"전송 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Sending)} / " +
        $"불확실 {Receipt.Steps.Count(s => s.State == HiperwallSendState.Unknown)}";
    public string Cancellation => !Receipt.Steps.Any(s => s.State == HiperwallSendState.Pending)
        ? "취소할 미전송 단계 없음" : CanCancel ? "미전송 부분 선택 취소 가능" : "사용권·전체 장비 제어 권한 필요";
    public string Details => $"작업: {Name}\n원 요청자: {Requester}\n원 세션: {Receipt.Requester.Id} | 사용권 세대: {Receipt.Request.Generation}\n" +
        $"접수: {Receipt.AcceptedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | 요청 ID: {Id}\n" +
        $"Controller: {Receipt.Endpoint} | 설정 v{Receipt.Request.ConfigurationVersion}\n{Receipt.Summary}\n{Cancellation}\n\n" +
        string.Join("\n", Receipt.Steps.Select((s, i) =>
            $"{i + 1}. {s.Command.InstanceId ?? "Hiperwall 전체"} | 콘텐츠: {s.Command.ContentValue ?? "-"} | Zone: {s.Command.ZoneId ?? "-"} | {s.State} · {s.Message}"));
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
            Set(ref _selectedHiperwallJob, value);
            Changed(nameof(HiperwallJobDetails));
            CancelHiperwallJobCommand.Raise();
        }
    }
    public string HiperwallJobsSummary => $"Hiperwall 진행·결과 확인 필요 {HiperwallJobs.Count}건";
    public string HiperwallJobDetails => SelectedHiperwallJob?.Details ??
        "Hiperwall 작업을 선택하면 원 요청자·대상·개별 결과와 취소 가능 범위를 확인할 수 있습니다.";
    public AsyncCommand CancelHiperwallJobCommand { get; private set; } = null!;
    private void InitializeHandover()
    {
        CancelHiperwallJobCommand = Command(async () =>
        {
            var result = await Client.Post<HiperwallEditReceipt>("/api/hiperwall/edits/cancel",
                new JobActionRequest(Generation, SelectedHiperwallJob!.Id));
            Message = $"Hiperwall 미전송 부분 취소 처리: {result.Summary}. 이미 전송한 명령은 결과 확인이 필요합니다.";
        }, () => CanControl && _state?.CanControlHiperwall == true &&
            SelectedHiperwallJob?.Receipt.Steps.Any(s => s.State == HiperwallSendState.Pending) == true);
    }
    private void RefreshHandover()
    {
        var selectedId = _selectedHiperwallJob?.Id;
        Replace(HiperwallJobs, (_state?.OutstandingHiperwallEdits ?? [])
            .Where(r => r.NeedsAttention).Select(r => new HiperwallJobRow(r, r.Requester.Id != _login?.Session.Id,
                CanControl && _state?.CanControlHiperwall == true && r.Steps.Any(s => s.State == HiperwallSendState.Pending))));
        _selectedHiperwallJob = HiperwallJobs.FirstOrDefault(r => r.Id == selectedId);
        Changed(nameof(SelectedHiperwallJob)); Changed(nameof(HiperwallJobDetails)); Changed(nameof(HiperwallJobsSummary));
    }
}
