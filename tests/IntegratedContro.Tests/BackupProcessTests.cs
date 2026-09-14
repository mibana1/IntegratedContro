using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class BackupProcessTests
{
    private static Process Start(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Test host failed to start");
    }
    private static async Task RunCommand(string exe, params string[] arguments)
    {
        using var process = Start(exe, arguments);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await output + await error);
    }
    [Fact]
    public async Task Online_backup_verify_restore_and_real_host_start_preserve_identity_without_replaying_work()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var exe = host.Process!.StartInfo.FileName;
        var (client, login) = await host.Login();
        var lease = await HostProcess.Post<Lease>(client, "/api/lease/acquire");
        var device = await HostProcess.Post<DeviceConfig>(client, "/api/devices",
            new DeviceRequest(lease.Generation, Guid.NewGuid(), Guid.NewGuid(), "test PC", "test lamp", "virtual", "virtual-light"));
        await HostProcess.Post<RoleBinding>(client, "/api/roles", new RoleRequest(lease.Generation, "light", device.Id));
        var job = await HostProcess.Post<Job>(client, "/api/jobs", new SubmitRequest(Guid.NewGuid(), lease.Generation, "light", DelayBeforeMs: 60000));
        var source = await HostProcess.State(client);
        var backup = await HostProcess.Post<BackupResult>(client, "/api/backups");
        Assert.True(backup.Manifest.IncludesHostConfiguration);
        Assert.Contains(backup.Manifest.Files, f => f.Name == "host-certificate.dpapi");
        await RunCommand(exe, "verify-backup", "--data", backup.Directory);
        var restoredPath = Path.Combine(host.Root, "artifacts", "process-tests", "restored-" + Guid.NewGuid().ToString("N"));
        await RunCommand(exe, "restore", "--data", restoredPath, "--from", backup.Directory);
        // Only the isolated source test host is stopped to release its HTTPS port.
        await host.Kill();
        using var restored = Start(exe, "run", "--data", restoredPath);
        try
        {
            using var check = host.NewClient();
            var ready = false;
            for (var i = 0; i < 100 && !ready; i++)
            {
                if (restored.HasExited) throw new InvalidOperationException(await restored.StandardError.ReadToEndAsync());
                try { using var response = await check.GetAsync("/health"); ready = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (!ready) await Task.Delay(50);
            }
            Assert.True(ready);
            using var oldToken = await client.GetAsync("/api/state"); Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
            var (fresh, relogin) = await host.Login();
            Assert.Equal(login.Session.UserId, relogin.Session.UserId);
            var state = await HostProcess.State(fresh);
            Assert.Equal(source.SiteId, state.SiteId); Assert.Equal(LeaseMode.RecoveryRequired, state.Lease.Mode);
            Assert.Equal(JobStatus.Interrupted, state.Jobs.Single(j => j.Id == job.Id).Status);
            Assert.Null(state.Jobs.Single(j => j.Id == job.Id).Steps.Single().SentAt);
            Assert.Contains(device.Id, state.UncertainDevices);
            var history = await HostProcess.Post<HistoryPage<Job>>(fresh, "/api/history/jobs", new HistoryRequest());
            Assert.Equal(job.Id, history.Items.Single().Value.Id);
            var audit = await HostProcess.Post<HistoryPage<AuditEntry>>(fresh, "/api/history/audit", new HistoryRequest());
            Assert.Contains(audit.Items, i => i.Value.Action == "BackupRestored");
        }
        finally { if (!restored.HasExited) { restored.Kill(entireProcessTree: true); await restored.WaitForExitAsync(); } }
    }

    private sealed class BlockingBackupStore(HostState initial) : IStateStore, IBackupStore
    {
        private HostState _state = JsonDefaults.Copy(initial);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HostState Load() => JsonDefaults.Copy(_state);
        public void Save(HostState state) => _state = JsonDefaults.Copy(state);
        public BackupResult CreateBackup(string purpose)
        {
            Started.TrySetResult(); Finish.Task.Wait(TimeSpan.FromSeconds(10));
            return new("test-only", new(1, 2, _state.SiteId, _state.Revision, DateTimeOffset.UtcNow, purpose, [], false));
        }
        public void Dispose() { }
    }
    [Fact]
    public async Task Slow_backup_does_not_block_heartbeat_and_duplicate_backups_are_rejected()
    {
        var hasher = new TestHasher();
        using var store = new BlockingBackupStore(new() { Initialized = true,
            Accounts = [new InitialAdministratorPolicy(hasher).Create("admin", Rig.Password)] });
        var service = new ControlService(store, hasher, new GateDriver(), new TestClock());
        var login = service.Login(new("admin", Rig.Password, Guid.NewGuid(), "PC"));
        var lease = service.Acquire(login.Token);
        var backup = Task.Run(() => service.CreateBackup(login.Token));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var heartbeat = await Task.Run(() => service.Heartbeat(login.Token, lease.Generation)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(LeaseMode.Held, heartbeat.Mode);
            Rig.Reject("backup_busy", () => service.CreateBackup(login.Token));
        }
        finally { store.Finish.TrySetResult(); await backup; }
    }
}
