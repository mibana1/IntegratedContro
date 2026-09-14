using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Tests;

public sealed class StorageFoundationTests
{
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IntegratedControTests", Guid.NewGuid().ToString());
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IntegratedControTests")) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(Path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Path, true);
        }
    }
    private static void Sql(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString); connection.Open();
        using var c = connection.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery();
    }
    private static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString); connection.Open();
        using var c = connection.CreateCommand(); c.CommandText = sql; return (T)Convert.ChangeType(c.ExecuteScalar()!, typeof(T));
    }
    private static string Legacy(string folder, HostState state)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(folder, "control.sqlite"), Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version=1;
            CREATE TABLE host_state(id INTEGER PRIMARY KEY, schema_version INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE virtual_values(pc_id TEXT NOT NULL,device_id TEXT NOT NULL,operation TEXT NOT NULL,value INTEGER NOT NULL,
                PRIMARY KEY(pc_id,device_id,operation));
            INSERT INTO host_state VALUES(1,1,$json);
            INSERT INTO virtual_values VALUES('pc','device','Power',1);
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, JsonDefaults.Options)); command.ExecuteNonQuery();
        return cs;
    }
    [Fact]
    public void Version_one_migrates_atomically_with_verified_backup_and_preserves_ids_and_histories()
    {
        using var r = new Rig(); r.Device(); r.Service.Submit(r.Admin.Token, r.Manual());
        var expected = r.Store.Load(); using var folder = new Folder(); var cs = Legacy(folder.Path, expected);
        using var migrated = new SqliteStateStore(folder.Path);
        Assert.Equal(2, Scalar<int>(cs, "PRAGMA user_version"));
        Assert.Equal(JsonSerializer.Serialize(expected, JsonDefaults.Options), JsonSerializer.Serialize(migrated.Load(), JsonDefaults.Options));
        var payload = Scalar<string>(cs, "SELECT payload FROM host_state");
        var checkpoint = JsonSerializer.Deserialize<HostState>(payload, JsonDefaults.Options)!;
        Assert.Empty(checkpoint.Jobs); Assert.Empty(checkpoint.Audit); Assert.Empty(checkpoint.HiperwallEdits);
        Assert.Equal(1, Scalar<int>(cs, "SELECT value FROM virtual_values"));
        var backup = Directory.GetDirectories(System.IO.Path.Combine(folder.Path, "backups")).Single();
        var manifest = SqliteStateStore.VerifyBackup(backup);
        Assert.Equal(1, manifest.DatabaseVersion); Assert.Equal(expected.SiteId, manifest.SiteId);
        migrated.Dispose();
        using var again = new SqliteStateStore(folder.Path);
        Assert.Single(Directory.GetDirectories(System.IO.Path.Combine(folder.Path, "backups")));
        Assert.Equal(expected.Jobs.Single().Id, again.Load().Jobs.Single().Id);
    }
    [Fact]
    public void Failed_migration_rolls_back_checkpoint_schema_and_version()
    {
        using var r = new Rig(); r.Device(); r.Service.Submit(r.Admin.Token, r.Manual());
        var bad = r.Store.Load(); bad.Jobs.Add(JsonDefaults.Copy(bad.Jobs.Single()));
        using var folder = new Folder(); var cs = Legacy(folder.Path, bad);
        Assert.Throws<InvalidDataException>(() => new SqliteStateStore(folder.Path));
        Assert.Equal(1, Scalar<int>(cs, "PRAGMA user_version"));
        Assert.Equal(0, Scalar<int>(cs, "SELECT count(*) FROM sqlite_master WHERE name='jobs'"));
        Assert.Equal(JsonSerializer.Serialize(bad, JsonDefaults.Options), Scalar<string>(cs, "SELECT payload FROM host_state"));
        Assert.Single(Directory.GetDirectories(System.IO.Path.Combine(folder.Path, "backups")));
    }
    [Fact]
    public void Unknown_database_version_is_not_downgraded_or_initialized()
    {
        using var r = new Rig(); var state = r.Store.Load();
        using var folder = new Folder(); var cs = Legacy(folder.Path, state); Sql(cs, "PRAGMA user_version=99;");
        Assert.Throws<InvalidOperationException>(() => new SqliteStateStore(folder.Path));
        Assert.Equal(99, Scalar<int>(cs, "PRAGMA user_version"));
        Assert.False(Directory.Exists(System.IO.Path.Combine(folder.Path, "backups")));
    }
    [Fact]
    public void History_rows_are_not_rewritten_and_failed_change_rolls_back_every_table()
    {
        using var r = new Rig(); r.Device(); r.Service.Submit(r.Admin.Token, r.Manual());
        var state = r.Store.Load();
        Sql(r.Store.ConnectionString, "CREATE TRIGGER reject_job_update BEFORE UPDATE ON jobs BEGIN SELECT RAISE(ABORT,'test failure'); END;");
        state.Audit.Add(new(r.Clock.GetUtcNow(), null, "Extra", "append only")); state.Revision++; r.Store.Save(state);
        var before = JsonSerializer.Serialize(r.Store.Load(), JsonDefaults.Options);
        state.Jobs.Single().Status = JobStatus.Cancelled; state.Audit.Add(new(r.Clock.GetUtcNow(), null, "MustRollback", "test")); state.Revision++;
        Assert.Throws<SqliteException>(() => r.Store.Save(state));
        Assert.Equal(before, JsonSerializer.Serialize(r.Store.Load(), JsonDefaults.Options));
        Sql(r.Store.ConnectionString, "DROP TRIGGER reject_job_update;");
        r.Store.Save(state);
        Assert.Equal(JobStatus.Cancelled, r.Store.Load().Jobs.Single().Status);
        Assert.Equal("MustRollback", r.Store.Load().Audit.Last().Action);
    }
    [Fact]
    public void Stored_audit_cannot_be_rewritten_or_deleted()
    {
        using var r = new Rig(); var state = r.Store.Load();
        state.Audit[0] = state.Audit[0] with { Detail = "tampered" };
        Assert.Throws<InvalidDataException>(() => r.Store.Save(state));
        state = r.Store.Load(); state.Audit.Clear();
        Assert.Throws<InvalidDataException>(() => r.Store.Save(state));
        Assert.NotEmpty(r.Store.Load().Audit);
    }
    [Fact]
    public void Cursor_pages_are_stable_under_new_inserts_and_state_keeps_old_outstanding_work()
    {
        using var r = new Rig(); r.Device(); var original = r.Service.Submit(r.Admin.Token, r.Manual());
        var state = r.Store.Load(); state.Jobs.Single().Status = JobStatus.NeedsReview;
        for (var i = 0; i < 205; i++)
        {
            var job = JsonDefaults.Copy(original); job.Id = Guid.NewGuid();
            job.Snapshot = job.Snapshot with { RequestId = Guid.NewGuid() }; job.Status = JobStatus.Completed;
            state.Jobs.Add(job);
        }
        r.Store.Save(state); r.Restart();
        Assert.Equal(101, r.Service.GetState(r.Admin.Token).Jobs.Length);
        Assert.Contains(r.Service.GetState(r.Admin.Token).Jobs, j => j.Id == original.Id);
        var first = r.Service.GetJobHistory(r.Admin.Token, new(Limit: 50));
        var seen = first.Items.Select(i => i.Value.Id).ToList();
        var updated = r.Store.Load(); var newest = JsonDefaults.Copy(original);
        newest.Id = Guid.NewGuid(); newest.Snapshot = newest.Snapshot with { RequestId = Guid.NewGuid() };
        updated.Jobs.Add(newest); r.Store.Save(updated);
        var next = first.NextBefore;
        while (next is not null)
        {
            var page = r.Store.Jobs(new(next, 50)); seen.AddRange(page.Items.Select(i => i.Value.Id)); next = page.NextBefore;
        }
        Assert.Equal(206, seen.Count); Assert.Equal(206, seen.Distinct().Count()); Assert.DoesNotContain(newest.Id, seen);
        Assert.Equal(newest.Id, r.Store.Jobs(new(Limit: 1)).Items.Single().Value.Id);
        Rig.Reject("invalid_page", () => r.Store.Jobs(new(Limit: 201)));
    }
    [Fact]
    public async Task Backup_includes_WAL_values_and_restore_blocks_even_never_sent_old_work()
    {
        using var r = new Rig(); var d = r.Device(); var queued = r.Service.Submit(r.Admin.Token, r.Manual());
        var driver = new VirtualDeviceDriver(r.Store.ConnectionString);
        var target = d with { ModelId = "virtual-light", LatencyMs = 0 };
        await driver.ExecuteAsync(new(new("light", d.Id, 1), target, DeviceOperation.Power, 1, "on/off", 0, 3000, FailurePolicy.Stop, null, null), default);
        var backup = r.Service.CreateBackup(r.Admin.Token);
        Assert.Equal(2, backup.Manifest.DatabaseVersion);
        Assert.Equal(r.Store.Load().SiteId, SqliteStateStore.VerifyBackup(backup.Directory).SiteId);
        using var restoredFolder = new Folder(); SqliteStateStore.RestoreBackup(backup.Directory, restoredFolder.Path);
        using var restored = new SqliteStateStore(restoredFolder.Path);
        var state = restored.Load();
        Assert.Equal(r.Store.Load().SiteId, state.SiteId);
        Assert.Equal(JobStatus.Interrupted, state.Jobs.Single(j => j.Id == queued.Id).Status);
        Assert.Equal(StepStatus.Skipped, state.Jobs.Single(j => j.Id == queued.Id).Steps.Single().Status);
        Assert.Equal(LeaseMode.RecoveryRequired, state.Lease.Mode); Assert.Contains(d.Id, state.UncertainDevices);
        Assert.Equal(1, (await new VirtualDeviceDriver(restored.ConnectionString).ReadAsync(target, default)).Values[DeviceOperation.Power]);
        var checkDriver = new GateDriver(); var service = new ControlService(restored, r.Hasher, checkDriver, r.Clock);
        Assert.False(await service.DispatchNextAsync()); Assert.Empty(checkDriver.Sent);
        Assert.Equal(JobStatus.Queued, r.Store.Load().Jobs.Single().Status); // Source remains operational.
        Assert.Throws<InvalidOperationException>(() => SqliteStateStore.RestoreBackup(backup.Directory, restoredFolder.Path));
    }
    [Fact]
    public void Backup_corruption_and_path_traversal_are_rejected_before_destination_writes()
    {
        using var r = new Rig(); var backup = r.Store.CreateBackup("manual");
        using var target = new Folder();
        var manifestPath = System.IO.Path.Combine(backup.Directory, "manifest.json");
        var traversal = backup.Manifest with { Files = [new("../control.sqlite", 0, "")] };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(traversal, JsonDefaults.Options));
        Assert.Throws<InvalidDataException>(() => SqliteStateStore.RestoreBackup(backup.Directory, target.Path));
        Assert.Empty(Directory.GetFileSystemEntries(target.Path));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(backup.Manifest, JsonDefaults.Options));
        File.AppendAllText(System.IO.Path.Combine(backup.Directory, "control.sqlite"), "corruption");
        Assert.Throws<InvalidDataException>(() => SqliteStateStore.VerifyBackup(backup.Directory));
    }
    [Fact]
    public void Backup_and_history_enforce_authentication_and_backup_admin_permission()
    {
        using var r = new Rig(); var viewer = r.Operator("viewer", AccountRole.Viewer);
        Assert.NotEmpty(r.Service.GetAuditHistory(viewer.Token, new()).Items);
        Rig.Reject("admin_required", () => r.Service.CreateBackup(viewer.Token));
        r.Service.Logout(viewer.Token);
        Rig.Reject("unauthorized", () => r.Service.GetAuditHistory(viewer.Token, new()));
        Rig.Reject("unauthorized", () => r.Service.GetJobHistory(viewer.Token, new()));
        Rig.Reject("unauthorized", () => r.Service.GetHiperwallHistory(viewer.Token, new()));
    }
}
