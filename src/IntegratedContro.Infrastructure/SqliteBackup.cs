using System.Security.Cryptography;
using System.Text.Json;
using IntegratedContro.Core;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Infrastructure;

public sealed partial class SqliteStateStore
{
    public BackupResult CreateBackup(string purpose)
    {
        if (purpose is not ("manual" or "pre-migration-v1")) throw new ArgumentException("지원하지 않는 백업 종류");
        HostState state;
        int version;
        var directory = ValidatePath(Path.Combine(DataPath, "backups",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{purpose}-{Guid.NewGuid():N}"), true);
        var target = Path.Combine(directory, "control.sqlite");
        // BackupDatabase includes WAL content; copying only the live .sqlite file is unsafe.
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            destination.Open();
            // A dedicated read connection lets lease heartbeats and state writes continue during a large backup.
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(DataPath, "control.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            source.Open(); source.BackupDatabase(destination); CheckIntegrity(destination);
            using var c = destination.CreateCommand(); c.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(c.ExecuteScalar());
            c.CommandText = "SELECT payload FROM host_state WHERE id=1;";
            state = ParseState(c.ExecuteScalar() as string ?? throw new InvalidDataException("백업할 호스트 상태가 없습니다."));
        }
        var names = new List<string> { "control.sqlite" };
        var hostNames = new[] { "host.json", "host-certificate.dpapi", "host-certificate.cer" };
        var hostCount = hostNames.Count(n => File.Exists(Path.Combine(DataPath, n)));
        if (hostCount != 0 && hostCount != hostNames.Length)
            throw new InvalidDataException("호스트 설정·인증서 파일이 일부 없습니다. 완전한 백업으로 처리하지 않습니다.");
        if (hostCount == hostNames.Length) names.AddRange(hostNames);
        if (state.Hiperwall?.CredentialId is { } id) names.Add($"hiperwall-{id:N}.dpapi");
        foreach (var name in names.Where(n => n != "control.sqlite"))
        {
            var source = Path.Combine(DataPath, name);
            if (!File.Exists(source) || File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("백업에 필요한 보호 저장 파일이 없거나 경로가 올바르지 않습니다.");
            File.Copy(source, Path.Combine(directory, name), overwrite: false);
        }
        var files = names.Select(n => DescribeFile(directory, n)).ToArray();
        var manifest = new BackupManifest(1, version, state.SiteId, state.Revision, DateTimeOffset.UtcNow,
            purpose, files, hostCount == hostNames.Length);
        // This file is the completion marker; incomplete directories cannot be restored.
        using (var stream = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, JsonDefaults.Options); stream.Flush(flushToDisk: true);
        }
        VerifyBackup(directory);
        return new(directory, manifest);
    }
    private static BackupFile DescribeFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        using var stream = File.OpenRead(path);
        return new(name, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
    private static bool AllowedBackupName(string name) =>
        name is "control.sqlite" or "host.json" or "host-certificate.dpapi" or "host-certificate.cer" ||
        (name.StartsWith("hiperwall-", StringComparison.Ordinal) && name.EndsWith(".dpapi", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[9..^6], "N", out _));
    public static BackupManifest VerifyBackup(string directory)
    {
        directory = ValidatePath(directory, false);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("백업 manifest 경로 오류");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), JsonDefaults.Options)
            ?? throw new InvalidDataException("백업 manifest 오류");
        if (manifest.FormatVersion != 1 || manifest.DatabaseVersion is not (1 or DatabaseVersion) ||
            manifest.Files is not { Length: >= 1 and <= 5 } ||
            manifest.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            !manifest.Files.Any(f => f.Name == "control.sqlite"))
            throw new InvalidDataException("지원하지 않거나 불완전한 백업입니다.");
        var hostNames = new[] { "host.json", "host-certificate.dpapi", "host-certificate.cer" };
        if (manifest.IncludesHostConfiguration != hostNames.All(n => manifest.Files.Any(f => f.Name == n)) ||
            (!manifest.IncludesHostConfiguration && hostNames.Any(n => manifest.Files.Any(f => f.Name == n))))
            throw new InvalidDataException("호스트 파일 목록 오류");
        foreach (var file in manifest.Files)
        {
            if (Path.GetFileName(file.Name) != file.Name || !AllowedBackupName(file.Name))
                throw new InvalidDataException("백업 파일 이름 오류");
            var path = Path.Combine(directory, file.Name);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || DescribeFile(directory, file.Name) != file)
                throw new InvalidDataException("백업 파일 손상 또는 누락: " + file.Name);
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "control.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); CheckIntegrity(connection);
        using var c = connection.CreateCommand(); c.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(c.ExecuteScalar()) != manifest.DatabaseVersion) throw new InvalidDataException("백업 DB 버전 불일치");
        c.CommandText = "SELECT payload FROM host_state WHERE id=1;";
        var state = ParseState(c.ExecuteScalar() as string ?? throw new InvalidDataException("백업 DB 상태 없음"));
        if (state.SiteId != manifest.SiteId || state.Revision != manifest.Revision)
            throw new InvalidDataException("백업 현장/버전 불일치");
        if (state.Hiperwall?.CredentialId is { } id && !manifest.Files.Any(f => f.Name == $"hiperwall-{id:N}.dpapi"))
            throw new InvalidDataException("백업 자격 증명 파일 누락");
        return manifest;
    }
    public static void RestoreBackup(string backupDirectory, string targetDirectory)
    {
        backupDirectory = ValidatePath(backupDirectory, false);
        var manifest = VerifyBackup(backupDirectory);
        targetDirectory = ValidatePath(targetDirectory, true);
        if (Directory.EnumerateFileSystemEntries(targetDirectory).Any())
            throw new InvalidOperationException("복원 대상은 비어 있는 새 로컬 폴더여야 합니다. 기존 운영 데이터는 덮어쓰지 않습니다.");
        using (var ownership = new FileStream(Path.Combine(targetDirectory, "host.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // Write the recovery marker before the first copied DB byte, including interrupted copies.
            using (var marker = new FileStream(Path.Combine(targetDirectory, "restore-pending.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(marker, manifest, JsonDefaults.Options); marker.Flush(flushToDisk: true);
            }
            foreach (var file in manifest.Files)
            {
                var source = Path.Combine(backupDirectory, file.Name);
                using var input = File.OpenRead(source);
                using var output = new FileStream(Path.Combine(targetDirectory, file.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output); output.Flush(flushToDisk: true);
            }
            foreach (var file in manifest.Files)
                if (DescribeFile(targetDirectory, file.Name) != file) throw new InvalidDataException("복원 중 파일 변경/손상");
            // The recovery marker remains until quarantine commits.
        }
        using var restored = new SqliteStateStore(targetDirectory);
        // Constructor completes quarantine before exposing the restored state.
        restored.Load();
    }
    private void QuarantineRestoredWork()
    {
        var marker = Path.Combine(DataPath, "restore-pending.json");
        if (!File.Exists(marker)) return;
        var state = Load();
        foreach (var job in state.Jobs.Where(j => j.Active))
        {
            foreach (var step in job.Steps)
            {
                if (step.Status is not (StepStatus.Pending or StepStatus.Waiting or StepStatus.Dispatching)) continue;
                var sending = step.Status == StepStatus.Dispatching;
                step.Status = sending ? StepStatus.Unknown : StepStatus.Skipped;
                step.Result = sending ? "백업 복원: 결과 대조 필요" : "백업 복원: 과거 명령 자동 실행 금지";
                step.FinishedAt = DateTimeOffset.UtcNow;
                step.Evidence = new(sending ? CommandOutcome.Unknown : CommandOutcome.Cancelled,
                    ConfirmationLevel.None, step.Result, step.FinishedAt.Value);
            }
            job.Status = job.Steps.Any(s => s.Status == StepStatus.Unknown) ? JobStatus.NeedsReview : JobStatus.Interrupted;
            job.Result = "백업 복원: 기록 보존 / 작업 자동 재실행 금지";
        }
        foreach (var edit in state.HiperwallEdits.Where(e => e.Active))
            foreach (var step in edit.Steps)
            {
                if (step.State == HiperwallSendState.Pending) { step.State = HiperwallSendState.Rejected; step.Message = "백업 복원: 자동 실행 금지"; }
                else if (step.State == HiperwallSendState.Sending) { step.State = HiperwallSendState.Unknown; step.Message = "백업 복원: 결과 대조 필요"; }
            }
        state.Lease.Mode = LeaseMode.RecoveryRequired; state.Lease.Generation++;
        state.Lease.LostAt = state.Lease.FencedAt = DateTimeOffset.UtcNow;
        state.Lease.RecoveryReason = "백업 복원: 실제 현재 상태와 관리자 복구 검토 필요";
        state.UncertainDevices = state.Devices.Select(d => d.Id).ToList();
        foreach (var device in state.DeviceStates.Values)
        {
            device.Simulated.Clear(); device.Observed.Clear(); device.Desired.Clear(); device.LastCommand = null;
            device.LastResult = "백업 복원 / 과거 명령 결과는 이력에서 확인";
            device.Connection = "백업 복원 / 현재 상태 재조회 필요";
            device.ConnectionStatus = DeviceConnectionStatus.RecoveryRequired;
        }
        state.Audit.Add(new(DateTimeOffset.UtcNow, null, "BackupRestored", "백업 이력 보존 / 접수 작업 자동 재실행 차단"));
        state.Revision++; Save(state);
        File.Delete(marker); // Only this verified restoration marker is removed after the durable commit.
    }
}
