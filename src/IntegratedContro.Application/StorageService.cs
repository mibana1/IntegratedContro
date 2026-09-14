using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private IHistoryStore HistoryStore(string token)
    {
        Healthy(); Authenticate(token);
        return _store as IHistoryStore ?? throw new DomainException("history_unavailable", "이력 조회를 지원하는 호스트가 필요합니다.", 503);
    }
    public HistoryPage<Job> GetJobHistory(string token, HistoryRequest request)
    { lock (_gate) { return HistoryStore(token).Jobs(request); } }
    public HistoryPage<HiperwallEditReceipt> GetHiperwallHistory(string token, HistoryRequest request)
    { lock (_gate) { return HistoryStore(token).HiperwallEdits(request); } }
    public HistoryPage<AuditEntry> GetAuditHistory(string token, HistoryRequest request)
    {
        lock (_gate)
        {
            var page = HistoryStore(token).Audit(request);
            return page with { Items = page.Items.Select(i => i with { Value = AuditPresentation.Enrich(i.Value, _state) }).ToArray() };
        }
    }
    private int _backingUp;
    public BackupResult CreateBackup(string token)
    {
        lock (_gate)
        {
            Healthy(); var session = Admin(_state, token);
            Require(_store is IBackupStore, "backup_unavailable", "백업을 지원하는 호스트가 필요합니다.", 503);
            Require(_backingUp == 0, "backup_busy", "이미 백업 중입니다. 완료 결과를 확인하세요.");
            // Authorize and audit once. The SQLite snapshot runs outside the lease coordination lock.
            var next = JsonDefaults.Copy(_state);
            Audit(next, session.Info.UserId, "BackupRequested", "관리자가 일관된 호스트 백업을 요청했습니다.");
            Persist(next); _backingUp = 1;
        }
        try { return ((IBackupStore)_store).CreateBackup("manual"); }
        finally { lock (_gate) _backingUp = 0; }
    }
}
