using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record JobRow(Job Job, bool PreviousSession)
{
    public Guid Id => Job.Id;
    public string Name => Job.Snapshot.Name;
    public string Requester => $"{Job.Snapshot.RequesterName} / {Job.Snapshot.ClientPcName}";
    public string OriginSession => Job.Snapshot.SessionId.ToString()[..8];
    public string Kind => Job.Kind == JobKind.LightSlot ? "장비 슬롯" : Job.IsLightBatch ? "일괄 조명" : Job.Kind == JobKind.Scenario ? "시나리오" : "일반 명령";
    public string Targets => string.Join(", ", Job.Snapshot.Steps.Select(x => x.TargetLabel).Distinct());
    public string Progress => $"{Job.Steps.Count(x => x.Status is not (StepStatus.Pending or StepStatus.Dispatching or StepStatus.Waiting))}/{Job.Steps.Count}";
    public string Status => Job.Status switch
    {
        JobStatus.Queued => "접수·대기", JobStatus.Running => "실행 중", JobStatus.StopRequested => "취소 요청·결과 대기",
        JobStatus.Completed => Job.Snapshot.Mode == "Virtual" ? "가상 실행 종료" : "실행 종료", JobStatus.Cancelled => "미전송 부분 취소", JobStatus.Interrupted => "중단",
        _ => "불확실·대조 필요"
    };
    public string AcceptedAt => Job.Snapshot.AcceptedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Result => Job.Result;
}
public sealed partial class JobManagementViewModel : FeatureViewModel
{
    private readonly IJobManagementHost _host;
    public ObservableCollection<JobRow> Jobs { get; } = [];
    public Func<string, bool> ConfirmManualSwitch { get; set; } = _ => false;
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand ManualSwitchCommand { get; }
    public JobManagementViewModel(IJobManagementHost host) : base(host)
    {
        _host = host;
        CancelCommand = Command(async () => { await _host.CancelJobAsync(new JobActionRequest(Generation, SelectedJob!.Id)); ReportStatus("선택 취소 요청을 처리했습니다. 전송된 동작의 물리 정지·롤백은 아닙니다."); },
            () => CanControl && SelectedJob is not null && (SelectedJob.Job.Active || SelectedJob.Job.Status == JobStatus.NeedsReview));
        ManualSwitchCommand = Command(async () =>
        {
            if (!ConfirmManualSwitch($"'{SelectedJob!.Name}' 전체의 후속 단계를 중단합니다.\n전송된 동작은 되돌리지 않습니다. 장비 상태를 대조하고 Hiperwall의 남은 전송·불확실 표시를 확인한 뒤 새 조작을 선택하세요.\n\n대상: {SelectedJob.Targets}")) return;
            await _host.SwitchToManualAsync(new JobActionRequest(Generation, SelectedJob.Id));
            ReportStatus("시나리오 후속 단계를 차단했습니다. 처리 종료 후 장비 상태를 대조하고 Hiperwall의 남은 전송·불확실 표시를 확인하세요.");
        }, () => CanControl && SelectedJob?.Job.Kind == JobKind.Scenario && SelectedJob.Job.Active);
        InitializeHandover();
    }
    protected override void OnContextChanged(FeatureContext previous)
    {
        if (!ReferenceEquals(previous.State, State))
        {
            var selectedId = previous.State?.Session.Id == State?.Session.Id ? SelectedJob?.Id : null;
            Replace(Jobs, (State?.Jobs ?? []).OrderByDescending(j => j.Snapshot.AcceptedAt)
                .Select(j => new JobRow(j, j.Snapshot.SessionId != State?.Session.Id)));
            SelectedJob = Jobs.FirstOrDefault(j => j.Id == selectedId);
        }
        if (previous.State?.Session.Id != State?.Session.Id) _selectedHiperwallJob = null;
        RefreshHandover(); Changed(nameof(PreviousSummary));
    }
    public string PreviousSummary => State is null ? "이전 사용자 작업 0건" :
        $"이전 사용자 작업 {State.Jobs.Count(j => j.Snapshot.SessionId != State?.Session.Id && (j.Active || j.Status == JobStatus.NeedsReview)) +
            State.OutstandingHiperwallEdits.Where(r => r.Requester.Id != State?.Session.Id && r.NeedsAttention).Select(r => r.Request.RequestId)
                .Union(State.OutstandingHiperwallDisplays.Where(j => j.Requester.Id != State?.Session.Id).Select(j => j.Request.RequestId)).Count()}건 · Hiperwall 포함 · 사용 종료 후에도 호스트에서 유지";
    private JobRow? _selectedJob;
    public JobRow? SelectedJob { get => _selectedJob; set { Set(ref _selectedJob, value); Changed(nameof(JobDetails)); RefreshCommands(); } }
    public string JobDetails
    {
        get
        {
            if (SelectedJob is null) return "작업을 선택하면 요청자·취소자·고정 대상·단계별 결과를 확인할 수 있습니다.";
            var job = SelectedJob.Job; var snapshot = job.Snapshot;
            return $"작업: {snapshot.Name} | {SelectedJob.Status}\n원 요청자: {snapshot.RequesterName} / {snapshot.ClientPcName} / 접수 {snapshot.AcceptedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                $"원 세션: {snapshot.SessionId} | 사용권 세대: {snapshot.LeaseGeneration}\n작업 ID: {job.Id} | 요청 ID: {snapshot.RequestId}\n" +
                $"취소 요청자: {job.CancellerName ?? "없음"} | 취소 시각: {job.CancelRequestedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                $"실행 결과: {job.Result}\n만료: {snapshot.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {snapshot.Mode} 모드 | 시나리오 버전: {snapshot.ScenarioVersion}\n\n" +
                string.Join("\n\n", snapshot.Steps.Select((step, i) => FormatScenarioStep(step, job.Steps[i], i)));
        }
    }
    private static string FormatScenarioStep(StepSnapshot step, StepRun run, int index)
    {
        var target = step.Display is { } d
            ? $"배치: {d.Layout.Name} v{d.Layout.Version} / 연결 v{d.Layout.ConfigurationVersion}\n   Controller: {d.Endpoint}\n   표시 요청 ID: {d.RequestId} / 대상 {d.Layout.Placements.Length}개 / 기간: {d.Layout.Duration}"
            : $"역할: {step.Role?.Id} → {step.TargetLabel}\n   고정 PC ID: {step.Target?.PcId} / 장비 ID: {step.Target?.Id}\n   장비 설정 v{step.Target?.Version} / 역할 v{step.Role?.Version}\n   {step.Operation} = {step.Value} {step.Unit} / 전송 전 조건: {step.ConditionOperation} = {step.ConditionValue}";
        return $"{index + 1}. {step.KindLabel}\n   {target}\n   시작 전 대기 {step.DelayBeforeMs}ms / 제한 {step.TimeoutMs}ms / 실패 정책 {step.OnFailure}\n" +
            $"   시작: {run.StartedAt?.ToLocalTime():HH:mm:ss} / 제한: {run.DeadlineAt?.ToLocalTime():HH:mm:ss}\n   {run.Status} / {run.Result}";
    }

}
