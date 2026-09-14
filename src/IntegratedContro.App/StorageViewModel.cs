using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record StoredHistoryRow(long Sequence, string Time, string Actor, string Summary, string Details);
public sealed partial class MainViewModel
{
    public string[] HistoryKinds { get; } = ["명령·시나리오", "Hiperwall 편집", "감사 기록"];
    private string _historyKind = "감사 기록";
    private long? _historyNextBefore;
    private Guid? _historySession;
    public string HistoryKind
    {
        get => _historyKind;
        set { if (Set(ref _historyKind, value)) { ClearHistory(); } }
    }
    public ObservableCollection<StoredHistoryRow> HistoryRows { get; } = [];
    private StoredHistoryRow? _selectedHistory;
    public StoredHistoryRow? SelectedHistory
    { get => _selectedHistory; set { Set(ref _selectedHistory, value); Changed(nameof(HistoryDetails)); } }
    public string HistoryDetails => SelectedHistory?.Details ?? "이력을 조회하고 항목을 선택하세요.";
    private string _historySummary = "종류를 선택한 뒤 최신 이력 조회를 누르세요. 한 번에 50건씩 조회합니다.";
    public string HistorySummary { get => _historySummary; private set => Set(ref _historySummary, value); }
    private string _backupSummary = "백업은 호스트의 지정 데이터 폴더 아래 backups에 저장합니다.";
    public string BackupSummary { get => _backupSummary; private set => Set(ref _backupSummary, value); }
    public AsyncCommand LoadHistoryCommand { get; private set; } = null!;
    public AsyncCommand OlderHistoryCommand { get; private set; } = null!;
    public AsyncCommand BackupCommand { get; private set; } = null!;
    private void InitializeStorage()
    {
        LoadHistoryCommand = Command(() => LoadHistory(null), () => _connected && _state?.HistorySupported == true);
        OlderHistoryCommand = Command(() => LoadHistory(_historyNextBefore),
            () => _connected && _state?.HistorySupported == true && _historyNextBefore is not null);
        BackupCommand = Command(async () =>
        {
            BackupSummary = "일관된 호스트 백업과 무결성 검증 중…";
            try
            {
                var result = await Client.Post<BackupResult>("/api/backups", timeoutMs: 300000);
                BackupSummary = $"백업·검증 완료: {result.Directory}\n현장 {result.Manifest.SiteId} · revision {result.Manifest.Revision} · {result.Manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                    "복원은 호스트 PC에서 새 빈 폴더로 수행합니다. 보호 저장 파일은 원래 Windows 실행 계정이 필요합니다.";
            }
            catch { BackupSummary = "백업 완료를 확인하지 못했습니다. 호스트 backups 폴더의 manifest와 검증 결과를 확인하세요."; throw; }
        }, () => IsAdmin && _state?.BackupSupported == true);
    }
    private void ClearHistory()
    {
        HistoryRows.Clear(); SelectedHistory = null; _historyNextBefore = null;
        HistorySummary = "종류를 선택한 뒤 최신 이력 조회를 누르세요. 한 번에 50건씩 조회합니다.";
        OlderHistoryCommand?.Raise();
    }
    private void RefreshStorageContext()
    {
        if (_historySession == _login?.Session.Id) return;
        _historySession = _login?.Session.Id;
        ClearHistory(); BackupSummary = "백업은 호스트의 지정 데이터 폴더 아래 backups에 저장합니다.";
    }
    private async Task LoadHistory(long? before)
    {
        var session = _login?.Session.Id; var kind = HistoryKind;
        StoredHistoryRow[] rows; long? next;
        var request = new HistoryRequest(before);
        if (kind == "명령·시나리오")
        {
            var page = await Client.Post<HistoryPage<Job>>("/api/history/jobs", request); next = page.NextBefore;
            rows = page.Items.Select(i =>
            {
                var j = new JobRow(i.Value, i.Value.Snapshot.SessionId != session);
                return new StoredHistoryRow(i.Sequence, j.AcceptedAt, j.Requester, $"{j.Name} · {j.Status}",
                    $"작업 ID: {j.Id}\n원 세션: {i.Value.Snapshot.SessionId}\n대상: {j.Targets}\n{j.Result}\n" +
                    string.Join("\n", i.Value.Steps.Select((s, n) =>
                        $"{n + 1}. {s.Status} · {DeviceEvidence.Label(s.Evidence?.Confirmation ?? ConfirmationLevel.None)} · {s.Result} · {DeviceEvidence.RecordedObservations(s.Evidence)}")));
            }).ToArray();
        }
        else if (kind == "Hiperwall 편집")
        {
            var page = await Client.Post<HistoryPage<HiperwallEditReceipt>>("/api/history/hiperwall", request); next = page.NextBefore;
            rows = page.Items.Select(i =>
            {
                var r = new HiperwallJobRow(i.Value, i.Value.Requester.Id != session, false);
                return new StoredHistoryRow(i.Sequence, r.AcceptedAt, r.Requester, i.Value.Summary, r.Details);
            }).ToArray();
        }
        else
        {
            var page = await Client.Post<HistoryPage<AuditEntry>>("/api/history/audit", request); next = page.NextBefore;
            rows = page.Items.Select(i =>
            {
                var r = new AuditRow(i.Value);
                return new StoredHistoryRow(i.Sequence, r.Time, r.Actor, $"{r.Event} · {r.Summary}", r.Raw);
            }).ToArray();
        }
        if (_login?.Session.Id != session || HistoryKind != kind) return;
        Replace(HistoryRows, rows); SelectedHistory = null; _historyNextBefore = next;
        HistorySummary = $"{kind} {rows.Length}건 · " + (next is null ? "마지막 페이지" : "이전 이력으로 계속 조회할 수 있습니다.");
    }
}
