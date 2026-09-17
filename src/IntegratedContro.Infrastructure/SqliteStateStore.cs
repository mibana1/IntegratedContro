using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Infrastructure;

/// <summary>A small-site aggregate checkpoint and audit history in a FULL-synchronous SQLite transaction.
/// File ownership is held before opening SQLite, including during setup and migration.</summary>
public sealed class SqliteStateStore : IStateStore
{
    private readonly FileStream _ownership;
    private readonly SqliteConnection _connection;
    public string DataPath { get; }
    public SqliteStateStore(string dataPath, bool initialize = false)
    {
        DataPath = LocalHostDataPath.Validate(dataPath, initialize);
        _ownership = new FileStream(Path.Combine(DataPath, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var dbPath = Path.Combine(DataPath, "control.sqlite");
            if (initialize && (File.Exists(dbPath) || File.Exists(Path.Combine(DataPath, "host.json")) ||
                File.Exists(Path.Combine(DataPath, "host-certificate.dpapi")) || File.Exists(Path.Combine(DataPath, "host-certificate.cer"))))
                throw new InvalidOperationException("기존 운영 파일이 있습니다. DB 유실이나 미완료 초기화를 새 현장으로 대체하지 않습니다.");
            if (!initialize && !File.Exists(dbPath)) throw new FileNotFoundException("지정 폴더의 DB가 없습니다. 다른 DB를 생성하지 않습니다.", dbPath);
            _connection = new(new SqliteConnectionStringBuilder { DataSource = dbPath,
                Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
                Pooling = false }.ToString());
            _connection.Open();
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            if (initialize)
            {
                using var schema = _connection.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE host_state (id INTEGER PRIMARY KEY CHECK (id=1), schema_version INTEGER NOT NULL, payload TEXT NOT NULL);
                    CREATE TABLE virtual_values (pc_id TEXT NOT NULL, device_id TEXT NOT NULL, operation TEXT NOT NULL,
                        value INTEGER NOT NULL, PRIMARY KEY(pc_id,device_id,operation));
                    PRAGMA user_version=1;
                    """;
                schema.ExecuteNonQuery();
            }
            else
            {
                using var version = _connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(version.ExecuteScalar()) != 1)
                    throw new InvalidOperationException("지원하지 않는 DB 버전입니다. 자동 초기화하지 않습니다.");
            }
        }
        catch { _connection?.Dispose(); _ownership.Dispose(); throw; }
    }
    public HostState Load()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM host_state WHERE id=1 AND schema_version=1";
        var json = command.ExecuteScalar() as string ?? throw new InvalidDataException("초기화 완료 DB가 아닙니다. 자동 초기화하지 않습니다.");
        var state = JsonSerializer.Deserialize<HostState>(json, JsonDefaults.Options) ?? throw new InvalidDataException("DB 상태 오류");
        if (state.SchemaVersion != 1 || !state.Initialized) throw new InvalidDataException("지원하지 않는 상태 또는 미완료 초기화입니다.");
        return state;
    }
    public void Save(HostState state)
    {
        var json = JsonSerializer.Serialize(state, JsonDefaults.Options);
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO host_state(id,schema_version,payload) VALUES(1,1,$payload)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload;
            """;
        command.Parameters.AddWithValue("$payload", json);
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public string ConnectionString => _connection.ConnectionString;
    public void Dispose() { _connection.Dispose(); _ownership.Dispose(); }
}
