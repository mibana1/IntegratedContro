using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class RecoveryViewModel : FeatureViewModel
{
    private readonly IRecoveryHost _host;
    private RecoveryReview? _review;
    public AsyncCommand ReviewCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public RecoveryViewModel(IRecoveryHost host) : base(host)
    {
        _host = host;
        ReviewCommand = Command(async () =>
        {
            var sessionId = State?.Session.Id; var generation = Generation;
            var review = await _host.ReviewAsync();
            if (!Context.Connected || State?.Session.Id != sessionId || Generation != generation || State?.Lease.Mode != LeaseMode.RecoveryRequired) return;
            _review = review;
            ReviewText = $"확인 시각: {_review.ReviewedAt:O}\n사용권 세대: {_review.Generation}\n불확실 장비: {string.Join(", ", _review.UncertainDevices)}\n\n" +
                string.Join("\n\n", _review.Jobs.Select(j => $"{j.Id} | {j.Snapshot.RequesterName} | {j.Snapshot.Name}\n{j.Status} | {j.Result}\n" +
                    string.Join(", ", j.Snapshot.Steps.Select(x => x.TargetLabel).Distinct())));
            ReviewText += "\n\nHiperwall 편집 · 진행/결과 확인 필요\n" + string.Join("\n\n", _review.HiperwallEdits.Select(r =>
                $"{r.Requester.UserName} / {r.Requester.PcName} · {r.Summary}\n요청 {r.Request.RequestId}\n" + string.Join("\n", r.Steps.Select(s => $"{s.Command.InstanceId}: {s.Message}"))));
            ReviewText += "\n\nHiperwall 표시·정리\n" + string.Join("\n\n", _review.HiperwallDisplays.Select(j => $"{j.Requester.UserName} / {j.Requester.PcName} · {j.Summary}\n{j.Schedule}\n{j.Endpoint}\n" + string.Join("\n", j.Targets.Select(t => $"{t.Command.InstanceId}: {t.Message}"))));
            Changed(nameof(ReviewText)); ReportStatus("진행 작업과 불확실 대상을 확인한 후 관리자 복구 인계를 승인하세요.");
        }, () => IsAdmin && State?.Lease.Mode == LeaseMode.RecoveryRequired);
        ApproveCommand = Command(async () =>
        {
            await _host.ApproveRecoveryAsync(new RecoveryApprovalRequest(_review!.ReviewId));
            _review = null; ReportStatus("관리자 복구 인계 완료. 다음 운영자는 사용 시작 후 남은 작업을 검토하세요.");
        }, () => IsAdmin && _review?.Generation == Generation && _review is not null && State?.Lease.Mode == LeaseMode.RecoveryRequired);
    }
    public string RecoverySummary => State?.Lease.Mode != LeaseMode.RecoveryRequired ? "복구 인계가 필요한 사용권이 없습니다." :
        $"1. 연결 이상: {State.Lease.LostAt:O}\n2. 이전 세션 차단 확인: {State.Lease.FencedAt:O}\n사유: {State.Lease.RecoveryReason}\n3. 진행 작업 확인 후 4. 관리자 복구 인계를 승인하세요.";
    public string ReviewText { get; private set; } = "진행 작업 확인 버튼을 누르면 그 시점의 작업·예약·불확실 대상을 표시합니다.";
    public ObservableCollection<AuditRow> Audit { get; } = [];
    private AuditRow? _selectedAudit;
    public AuditRow? SelectedAudit { get => _selectedAudit; set { Set(ref _selectedAudit, value); Changed(nameof(AuditDetails)); } }
    public string AuditDetails => SelectedAudit?.Raw ?? "기록을 선택하면 원본 이벤트 코드와 ID를 확인할 수 있습니다.";
    protected override void OnContextChanged(FeatureContext previous)
    {
        if (previous.State?.Session.Id != State?.Session.Id || !Context.Connected || Context.Closing ||
            State?.Lease.Mode != LeaseMode.RecoveryRequired || _review is { } review && review.Generation != Generation)
        {
            _review = null;
            ReviewText = "진행 작업 확인 버튼을 누르면 그 시점의 작업·예약·불확실 대상을 표시합니다.";
            Changed(nameof(ReviewText));
        }
        if (!ReferenceEquals(previous.State, State))
        {
            var selected = previous.State?.Session.Id == State?.Session.Id ? SelectedAudit?.Entry : null;
            Replace(Audit, (State?.Audit ?? []).Reverse().Select(a => new AuditRow(a)));
            SelectedAudit = Audit.FirstOrDefault(a => a.Entry == selected);
        }
        Changed(nameof(RecoverySummary));
    }
}
