using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void Setup_hashes_password_and_rejects_duplicate_initialization()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var user = new InitialAdministratorPolicy(hasher).Create("first-admin", Rig.Password);
        Assert.DoesNotContain(Rig.Password, user.PasswordHash);
        Assert.True(hasher.Verify(Rig.Password, user.PasswordHash));
        Assert.False(hasher.Verify("wrong-password", user.PasswordHash));
        Assert.False(hasher.Verify("anything", "broken"));
        using var r = new Rig();
        r.Store.Dispose();
        Assert.Throws<InvalidOperationException>(() => new SqliteStateStore(r.DirectoryPath, true));
        using var reopened = new SqliteStateStore(r.DirectoryPath);
        Assert.True(reopened.Load().Initialized);
        reopened.Dispose(); // Release before fixture cleanup.
    }
    [Fact]
    public void Missing_existing_path_never_creates_a_replacement_database()
    {
        var path = Path.Combine(Path.GetTempPath(), "IntegratedControTests", Guid.NewGuid().ToString());
        Assert.Throws<DirectoryNotFoundException>(() => new SqliteStateStore(path));
        Assert.False(Directory.Exists(path));
        Assert.Throws<ArgumentException>(() => new SqliteStateStore("relative-data"));
        Assert.Throws<ArgumentException>(() => new SqliteStateStore(@"\\server\share"));
    }
    [Fact]
    public void Same_data_folder_allows_only_one_host_owner()
    {
        using var r = new Rig();
        Assert.Throws<IOException>(() => new SqliteStateStore(r.DirectoryPath));
        Assert.True(r.Store.Load().Initialized);
    }
    [Fact]
    public async Task Restart_recovers_configuration_queued_manual_and_stops_every_scenario()
    {
        using var r = new Rig(); var d = r.Device(); r.Device("other");
        var originalUser = r.Admin.Session.UserId;
        var manual = r.Service.Submit(r.Admin.Token, r.Manual("other"));
        var scenario = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 30000)));
        r.Restart();
        var state = r.Service.GetState(r.Admin.Token);
        Assert.Contains(state.Devices, x => x.Id == d.Id);
        Assert.Contains(state.Accounts, x => x.Id == originalUser);
        Assert.Equal(LeaseMode.RecoveryRequired, state.Lease.Mode);
        Assert.Equal(JobStatus.Interrupted, state.Jobs.Single(j => j.Id == scenario.Id).Status);
        await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(manual.Id).Status);
        Assert.Single(r.Driver.Sent);
    }
    [Fact]
    public async Task Persisted_dispatch_intent_is_unknown_after_restart_and_never_replayed()
    {
        using var r = new Rig(); var d = r.Device();
        var job = r.Service.Submit(r.Admin.Token, r.Manual());
        var durable = r.Store.Load(); var committed = durable.Jobs.Single(j => j.Id == job.Id);
        committed.Status = JobStatus.Running; committed.Steps[0].Status = StepStatus.Dispatching;
        committed.Steps[0].SentAt = r.Clock.GetUtcNow(); r.Store.Save(durable);
        r.Restart();
        Assert.Equal(JobStatus.NeedsReview, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Unknown, r.Job(job.Id).Steps[0].Status);
        Assert.Contains(d.Id, r.Service.GetState(r.Admin.Token).UncertainDevices);
        Assert.False(await r.Service.DispatchNextAsync());
        Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Cancelled_scenario_stays_cancelled_after_restart()
    {
        using var r = new Rig(); r.Device();
        var scenario = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1)));
        r.Service.Cancel(r.Admin.Token, new(r.Generation, scenario.Id));
        r.Restart();
        Assert.Equal(JobStatus.Cancelled, r.Job(scenario.Id).Status);
        Assert.False(await r.Service.DispatchNextAsync());
    }
    [Fact]
    public void Failed_commit_never_acknowledges_acceptance_and_fails_closed()
    {
        var hasher = new TestHasher();
        var initial = new HostState { Initialized = true, Accounts = [new InitialAdministratorPolicy(hasher).Create("admin", Rig.Password)] };
        using var store = new FailingStore(initial);
        var service = new ControlService(store, hasher, new GateDriver());
        var login = service.Login(new("admin", Rig.Password, Guid.NewGuid(), Environment.MachineName));
        var lease = service.Acquire(login.Token);
        var d = service.SaveDevice(login.Token, new(lease.Generation, Guid.NewGuid(), Guid.NewGuid(), Environment.MachineName, "light", "shared", "test"));
        service.SaveRole(login.Token, new(lease.Generation, "light", d.Id));
        store.FailNext = true;
        Assert.Throws<IOException>(() => service.Submit(login.Token, new(Guid.NewGuid(), lease.Generation, "light")));
        Assert.Empty(store.Load().Jobs);
        Rig.Reject("host_unavailable", () => service.Submit(login.Token, new(Guid.NewGuid(), lease.Generation, "light")));
    }
    private sealed class FailingStore(HostState state) : IStateStore
    {
        private HostState _state = JsonDefaults.Copy(state);
        public bool FailNext { get; set; }
        public HostState Load() => JsonDefaults.Copy(_state);
        public void Save(HostState s) { if (FailNext) throw new IOException("Injected disk failure"); _state = JsonDefaults.Copy(s); }
        public void Dispose() { }
    }
    [Fact]
    public async Task Virtual_driver_response_loss_changes_only_simulation_and_readback_survives_restart()
    {
        using var r = new Rig();
        var driver = new VirtualDeviceDriver(r.Store.ConnectionString);
        var target = new DeviceConfig(Guid.NewGuid(), Guid.NewGuid(), Environment.MachineName, "simulator", "test",
            "virtual-light", 1, true, VirtualFault.ResponseLost, 0);
        var step = new StepSnapshot(new("role", target.Id, 1), target, DeviceOperation.Brightness, 42, "%", 0, 100,
            FailurePolicy.Stop, null, null);
        using (var timeout = new CancellationTokenSource(100))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ExecuteAsync(step, timeout.Token));
        var reading = await driver.ReadAsync(target, CancellationToken.None);
        Assert.True(reading.Available); Assert.Equal(42, reading.Values[DeviceOperation.Brightness]);
        var otherPc = await driver.ReadAsync(target with { PcId = Guid.NewGuid() }, CancellationToken.None);
        Assert.Equal(0, otherPc.Values[DeviceOperation.Brightness]); // Same device ID/name is not another PC's target.
        r.Restart();
        var restored = await new VirtualDeviceDriver(r.Store.ConnectionString).ReadAsync(target, CancellationToken.None);
        Assert.Equal(42, restored.Values[DeviceOperation.Brightness]);
    }
    [Fact]
    public void Virtual_model_replacement_enforces_required_capability()
    {
        using var r = new Rig();
        var service = new ControlService(r.Store, r.Hasher, new VirtualDeviceDriver(r.Store.ConnectionString), r.Clock);
        var admin = service.Login(new("admin", Rig.Password, Guid.NewGuid(), Environment.MachineName));
        var review = service.ReviewRecovery(admin.Token); service.ApproveRecovery(admin.Token, review.ReviewId);
        var lease = service.Acquire(admin.Token);
        var full = service.SaveDevice(admin.Token, new(lease.Generation, Guid.NewGuid(), Guid.NewGuid(), Environment.MachineName,
            "dimmer", "connection", "virtual-light"));
        var basic = service.SaveDevice(admin.Token, new(lease.Generation, Guid.NewGuid(), Guid.NewGuid(), Environment.MachineName,
            "basic", "connection", "virtual-light-basic"));
        service.SaveRole(admin.Token, new(lease.Generation, "light", full.Id));
        service.SaveScenario(admin.Token, new(lease.Generation, Guid.NewGuid(), "dimming", [new("light", DeviceOperation.Brightness, 50)]));
        Rig.Reject("unsupported", () => service.SaveRole(admin.Token, new(lease.Generation, "light", basic.Id, 1)));
        Rig.Reject("value_range", () => service.Submit(admin.Token, new(Guid.NewGuid(), lease.Generation, "light", DeviceOperation.Brightness, 101)));
    }
}
