using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Infrastructure;

/// <summary>One FULL-synchronous transaction commits configuration and changed history rows.</summary>
public sealed partial class SqliteStateStore : IStateStore, IHistoryStore, IBackupStore
{
    public const int DatabaseVersion = 2;
    private readonly FileStream _ownership;
    private readonly SqliteConnection _connection;
    private readonly object _storeGate = new();
    private Dictionary<Guid, string> _jobs = [];
    private Dictionary<Guid, string> _edits = [];
    private List<string> _audit = [];
    public string DataPath { get; }
    public SqliteStateStore(string dataPath, bool initialize = false)
    {
        DataPath = ValidatePath(dataPath, initialize);
        _ownership = new FileStream(Path.Combine(DataPath, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var dbPath = Path.Combine(DataPath, "control.sqlite");
            if (initialize && (File.Exists(dbPath) || File.Exists(Path.Combine(DataPath, "host.json")) ||
                File.Exists(Path.Combine(DataPath, "host-certificate.dpapi")) || File.Exists(Path.Combine(DataPath, "host-certificate.cer"))))
                throw new InvalidOperationException("기존 운영 파일이 있습니다. 새 현장으로 대체하지 않습니다.");
            if (!initialize && !File.Exists(dbPath)) throw new FileNotFoundException("지정 폴더의 DB가 없습니다. 다른 DB를 생성하지 않습니다.", dbPath);
            _connection = new(new SqliteConnectionStringBuilder { DataSource = dbPath,
                Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            _connection.Open();
            Execute("PRAGMA busy_timeout=5000;");
            if (!initialize)
            {
                var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
                if (version is not (1 or DatabaseVersion))
                    throw new InvalidOperationException("지원하지 않는 DB 버전입니다. 자동 초기화하지 않습니다.");
                CheckIntegrity(_connection);
                if (version == 1) MigrateVersionOne();
            }
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            if (initialize)
            {
                using var tx = _connection.BeginTransaction();
                Execute("""
                    CREATE TABLE host_state (id INTEGER PRIMARY KEY CHECK(id=1), schema_version INTEGER NOT NULL, payload TEXT NOT NULL);
                    CREATE TABLE virtual_values (pc_id TEXT NOT NULL, device_id TEXT NOT NULL, operation TEXT NOT NULL,
                        value INTEGER NOT NULL, PRIMARY KEY(pc_id,device_id,operation));
                    """, tx);
                CreateHistorySchema(tx);
                Execute("PRAGMA user_version=2;", tx); tx.Commit();
            }
            else { Load(); QuarantineRestoredWork(); }
        }
        catch { _connection?.Dispose(); _ownership.Dispose(); throw; }
    }
    public static string ValidatePath(string path, bool initialize)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") ||
            path.StartsWith("//") || path.StartsWith(@"\?"))
            throw new ArgumentException("명시적인 로컬 절대 데이터 폴더가 필요합니다.");
        var full = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new ArgumentException("고정 로컬 디스크의 데이터 폴더를 선택하세요.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("재분석 지점/심볼릭 링크 데이터 경로는 지원하지 않습니다.");
        if (!Directory.Exists(full))
        {
            if (!initialize) throw new DirectoryNotFoundException("설정한 데이터 폴더가 없습니다. 경로를 복구하세요.");
            Directory.CreateDirectory(full);
        }
        return full;
    }
    private object? Scalar(string sql)
    {
        using var c = _connection.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar();
    }
    private void Execute(string sql, SqliteTransaction? transaction = null)
    {
        using var c = _connection.CreateCommand(); c.Transaction = transaction; c.CommandText = sql; c.ExecuteNonQuery();
    }
    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var c = connection.CreateCommand(); c.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(c.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("DB 무결성 검사 실패: 자동 복구/초기화하지 않습니다.");
    }
    private static HostState ParseState(string json)
    {
        var state = JsonSerializer.Deserialize<HostState>(json, JsonDefaults.Options) ?? throw new InvalidDataException("DB 상태 오류");
        if (state.SchemaVersion != 1 || !state.Initialized || !state.Accounts.Any(a => a.Role == AccountRole.Administrator))
            throw new InvalidDataException("지원하지 않는 상태 또는 미완료 초기화입니다.");
        return state;
    }
    private void CreateHistorySchema(SqliteTransaction tx)
    {
        Execute("""
            CREATE TABLE jobs (sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE,
                request_id TEXT NOT NULL UNIQUE, accepted_at TEXT NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE hiperwall_edits (sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE,
                accepted_at TEXT NOT NULL, needs_attention INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE audit_history (sequence INTEGER PRIMARY KEY AUTOINCREMENT, payload TEXT NOT NULL);
            CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
            CREATE INDEX jobs_status ON jobs(status,sequence);
            CREATE INDEX edits_attention ON hiperwall_edits(needs_attention,sequence);
            INSERT INTO schema_migrations VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """, tx);
    }
    private void MigrateVersionOne()
    {
        var json = Scalar("SELECT payload FROM host_state WHERE id=1 AND schema_version=1;") as string
            ?? throw new InvalidDataException("버전 1 상태를 읽을 수 없습니다.");
        var state = ParseState(json);
        // A verified backup is completed before any schema mutation. DDL and data move commit together.
        CreateBackup("pre-migration-v1");
        using var tx = _connection.BeginTransaction();
        CreateHistorySchema(tx);
        WriteCheckpoint(state, tx);
        WriteHistories(state, tx);
        Execute("PRAGMA user_version=2;", tx);
        tx.Commit();
    }
    public HostState Load()
    {
        lock (_storeGate)
        {
            var json = Scalar("SELECT payload FROM host_state WHERE id=1 AND schema_version=2;") as string
                ?? throw new InvalidDataException("초기화 완료 DB가 아닙니다. 자동 초기화하지 않습니다.");
            var state = ParseState(json);
            if (state.Jobs.Count != 0 || state.Audit.Count != 0 || state.HiperwallEdits.Count != 0)
                throw new InvalidDataException("분리된 이력과 checkpoint가 중복되어 있습니다.");
            state.Jobs = ReadAll<Job>("jobs");
            state.HiperwallEdits = ReadAll<HiperwallEditReceipt>("hiperwall_edits");
            state.Audit = ReadAll<AuditEntry>("audit_history");
            Cache(state); return state;
        }
    }
    private List<T> ReadAll<T>(string table)
    {
        using var c = _connection.CreateCommand(); c.CommandText = $"SELECT payload FROM {table} ORDER BY sequence;";
        using var reader = c.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonDefaults.Options) ?? throw new InvalidDataException("이력 JSON 오류"));
        return result;
    }
    private void Cache(HostState state)
    {
        _jobs = state.Jobs.ToDictionary(j => j.Id, Serialize);
        _edits = state.HiperwallEdits.ToDictionary(r => r.Request.RequestId, Serialize);
        _audit = state.Audit.Select(Serialize).ToList();
    }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
    private void WriteCheckpoint(HostState state, SqliteTransaction tx)
    {
        using var c = _connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = """
            INSERT INTO host_state(id,schema_version,payload) VALUES(1,2,$payload)
            ON CONFLICT(id) DO UPDATE SET schema_version=2,payload=excluded.payload;
            """;
        c.Parameters.AddWithValue("$payload", Serialize(state.WithoutHistory())); c.ExecuteNonQuery();
    }
    private void WriteHistories(HostState state, SqliteTransaction tx)
    {
        if (state.Jobs.Select(j => j.Id).Distinct().Count() != state.Jobs.Count ||
            state.HiperwallEdits.Select(r => r.Request.RequestId).Distinct().Count() != state.HiperwallEdits.Count ||
            _jobs.Keys.Except(state.Jobs.Select(j => j.Id)).Any() || _edits.Keys.Except(state.HiperwallEdits.Select(r => r.Request.RequestId)).Any() ||
            state.Audit.Count < _audit.Count)
            throw new InvalidDataException("이력 삭제·중복 ID는 허용하지 않습니다.");
        foreach (var job in state.Jobs)
        {
            var json = Serialize(job); if (_jobs.GetValueOrDefault(job.Id) == json) continue;
            using var c = _connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = """
                INSERT INTO jobs(id,request_id,accepted_at,status,payload) VALUES($id,$request,$at,$status,$payload)
                ON CONFLICT(id) DO UPDATE SET status=excluded.status,payload=excluded.payload;
                """;
            c.Parameters.AddWithValue("$id", job.Id.ToString()); c.Parameters.AddWithValue("$request", job.Snapshot.RequestId.ToString());
            c.Parameters.AddWithValue("$at", job.Snapshot.AcceptedAt.ToString("O")); c.Parameters.AddWithValue("$status", job.Status.ToString());
            c.Parameters.AddWithValue("$payload", json); c.ExecuteNonQuery();
        }
        foreach (var edit in state.HiperwallEdits)
        {
            var json = Serialize(edit); if (_edits.GetValueOrDefault(edit.Request.RequestId) == json) continue;
            using var c = _connection.CreateCommand(); c.Transaction = tx;
            c.CommandText = """
                INSERT INTO hiperwall_edits(id,accepted_at,needs_attention,payload) VALUES($id,$at,$attention,$payload)
                ON CONFLICT(id) DO UPDATE SET needs_attention=excluded.needs_attention,payload=excluded.payload;
                """;
            c.Parameters.AddWithValue("$id", edit.Request.RequestId.ToString()); c.Parameters.AddWithValue("$at", edit.AcceptedAt.ToString("O"));
            c.Parameters.AddWithValue("$attention", edit.NeedsAttention); c.Parameters.AddWithValue("$payload", json); c.ExecuteNonQuery();
        }
        for (var i = 0; i < state.Audit.Count; i++)
        {
            var json = Serialize(state.Audit[i]);
            if (i < _audit.Count)
            {
                if (_audit[i] != json) throw new InvalidDataException("기존 감사 이력은 수정할 수 없습니다.");
                continue;
            }
            using var c = _connection.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO audit_history(payload) VALUES($payload);";
            c.Parameters.AddWithValue("$payload", json); c.ExecuteNonQuery();
        }
    }
    public void Save(HostState state)
    {
        lock (_storeGate)
        {
            if (state.SchemaVersion != 1 || !state.Initialized) throw new InvalidDataException("저장 상태 버전/초기화 오류");
            using var tx = _connection.BeginTransaction();
            WriteCheckpoint(state, tx); WriteHistories(state, tx); tx.Commit();
            Cache(state); // Never update the cache until every row and checkpoint are durable.
        }
    }
    private HistoryPage<T> Page<T>(string table, HistoryRequest request)
    {
        if (request.Limit is < 1 or > 200 || request.Before is <= 0)
            throw new DomainException("invalid_page", "페이지 크기는 1~200, 커서는 양수여야 합니다.", 400);
        lock (_storeGate)
        {
            using var c = _connection.CreateCommand();
            c.CommandText = $"SELECT sequence,payload FROM {table} WHERE ($before IS NULL OR sequence < $before) ORDER BY sequence DESC LIMIT $limit;";
            c.Parameters.AddWithValue("$before", (object?)request.Before ?? DBNull.Value); c.Parameters.AddWithValue("$limit", request.Limit + 1);
            using var reader = c.ExecuteReader(); var rows = new List<HistoryItem<T>>();
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), JsonSerializer.Deserialize<T>(reader.GetString(1), JsonDefaults.Options)!));
            var more = rows.Count > request.Limit; if (more) rows.RemoveAt(rows.Count - 1);
            return new(rows.ToArray(), more ? rows[^1].Sequence : null);
        }
    }
    public HistoryPage<Job> Jobs(HistoryRequest request) => Page<Job>("jobs", request);
    public HistoryPage<HiperwallEditReceipt> HiperwallEdits(HistoryRequest request) => Page<HiperwallEditReceipt>("hiperwall_edits", request);
    public HistoryPage<AuditEntry> Audit(HistoryRequest request) => Page<AuditEntry>("audit_history", request);
    public string ConnectionString => _connection.ConnectionString;
    public void Dispose() { _connection.Dispose(); _ownership.Dispose(); }
}
