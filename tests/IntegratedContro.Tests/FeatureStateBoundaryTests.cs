using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class FeatureStateBoundaryTests
{
    [Fact]
    public void Foreign_queries_are_deeply_detached_even_inside_a_write_transaction()
    {
        using var fixture = new BoundaryFixture();
        var host = fixture.Host;
        var devices = host.DeviceAccess(); var scenarios = host.ScenarioAccess(); var wall = host.HiperwallAccess();
        var original = fixture.Store.Load();
        devices.Change(s => {
            s.Jobs[0].Steps[0].Result = "attempted cross-feature write";
            s.Jobs[0].Snapshot.Steps[0].Target!.DriverOptions["injected"] = "x";
            s.Scenarios[0].Steps[0] = s.Scenarios[0].Steps[0] with { Value = 999 };
            s.LightLayout = new(1, [original.Devices[0].Id]);
            return true;
        });
        scenarios.Change(s => {
            s.DeviceStates[original.Devices[0].Id].Desired[DeviceOperation.Power] = 999;
            s.LightLayout.DeviceIds[0] = Guid.NewGuid();
            s.Jobs[0].Result = "owned change";
            return true;
        });
        wall.Change(s => { s.Jobs[0].Result = "attempted wall write"; return true; });
        var saved = fixture.Store.Load();
        Assert.Equal(original.Jobs[0].Steps[0].Result, saved.Jobs[0].Steps[0].Result);
        Assert.Equal("owned change", saved.Jobs[0].Result);
        Assert.Equal(original.Scenarios[0].Steps[0].Value, saved.Scenarios[0].Steps[0].Value);
        Assert.DoesNotContain("injected", saved.Jobs[0].Snapshot.Steps[0].Target!.DriverOptions.Keys);
        Assert.Empty(saved.DeviceStates[original.Devices[0].Id].Desired);
        Assert.Equal(original.Devices[0].Id, Assert.Single(saved.LightLayout.DeviceIds));
    }

    [Fact]
    public void Resolved_device_step_does_not_leak_mutable_configuration_into_another_service()
    {
        using var fixture = new BoundaryFixture();
        var deviceAccess = fixture.Host.DeviceAccess();
        var service = new DeviceExecutionService(deviceAccess, new DeviceDriverRegistry(new GateDriver()));
        fixture.Host.ScenarioAccess().Change(s => {
            var user = deviceAccess.FindAccount(s, fixture.Store.Load().Accounts[0].Id)!;
            var step = service.Resolve(s, user, s.Scenarios[0].Steps[0]);
            step.Target!.DriverOptions["injected"] = "value";
            step.Target.Connection.Options["injected"] = "value";
            return true;
        });
        var saved = fixture.Store.Load();
        Assert.Empty(saved.Devices[0].DriverOptions);
        Assert.Empty(saved.Devices[0].Connection.Options);
    }

    [Fact]
    public void Read_snapshots_and_retained_committed_objects_cannot_change_authoritative_state()
    {
        using var fixture = new BoundaryFixture();
        var access = fixture.Host.DeviceAccess();
        DeviceStateScope retained;
        using (access.Open())
        {
            var read = access.Current;
            read.Devices.Clear();
            Assert.Throws<InvalidOperationException>(() => access.Commit(read));
            retained = access.Draft();
            Assert.NotEmpty(retained.Devices);
            retained.LightLayout = new(1, [retained.Devices[0].Id]);
            access.Commit(retained);
            retained.Devices.Clear();
            Assert.Throws<InvalidOperationException>(() => access.Commit(retained));
        }
        using (access.Open()) Assert.NotEmpty(access.Current.Devices);
        Assert.NotEmpty(fixture.Store.Load().Devices);
    }

    [Fact]
    public void Context_cannot_be_reused_after_scope_exit_or_from_another_host_or_revision()
    {
        using var fixture = new BoundaryFixture();
        using var other = new BoundaryFixture();
        var access = fixture.Host.DeviceAccess(); var cameras = fixture.Host.CameraAccess();
        DeviceStateScope closed;
        Assert.Throws<InvalidOperationException>(() => access.Draft());
        using (access.Open()) closed = access.Draft();
        using (access.Open())
        {
            Assert.Throws<InvalidOperationException>(() => access.Commit(closed));
            var current = access.Draft();
            using (other.Host.Open()) Assert.Throws<InvalidOperationException>(() => other.Host.DeviceAccess().For(current));
            cameras.Change(s => { s.Media = Media(); return true; });
            Assert.Throws<InvalidOperationException>(() => access.Commit(current));
        }
        Assert.Equal(1, fixture.Store.Load().Media!.Version);
    }

    [Fact]
    public void Account_queries_have_no_password_and_cannot_mutate_permissions()
    {
        using var fixture = new BoundaryFixture();
        var initial = fixture.Store.Load(); var id = initial.Accounts[0].Id;
        var access = fixture.Host.DeviceAccess();
        using (access.Open())
        {
            var account = access.FindAccount(access.Current, id)!;
            Assert.Null(account.GetType().GetProperty("PasswordHash"));
            account.DeviceIds[0] = Guid.NewGuid();
            Assert.Equal(initial.Accounts[0].DeviceIds, access.FindAccount(access.Current, id)!.DeviceIds);
        }
        Assert.Equal(initial.Accounts[0].DeviceIds, fixture.Store.Load().Accounts[0].DeviceIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Joined_changes_commit_once_or_rollback_together_on_storage_failure(bool fail)
    {
        using var fixture = new BoundaryFixture();
        var host = fixture.Host; var scenarios = host.ScenarioAccess(); var devices = host.DeviceAccess(); var wall = host.HiperwallAccess();
        var jobs = new ScenarioJobLifecycle(scenarios, new HiperwallJobLifecycle(wall));
        var before = fixture.Store.Load();
        using (scenarios.Open())
        {
            var draft = scenarios.Draft();
            var job = draft.Jobs[0];
            jobs.StopJob(draft, job.Id, new(new(Guid.NewGuid(), before.Accounts[0].Id, "admin", AccountRole.Administrator, Guid.NewGuid(), "test")), "test");
            devices.For(draft).UncertainDevices.Add(before.Devices[0].Id);
            fixture.Store.Fail = fail;
            if (fail) Assert.Throws<IOException>(() => scenarios.Commit(draft));
            else scenarios.Commit(draft);
        }
        var after = fixture.Store.Load();
        if (fail)
        {
            Assert.Equal(Json(before), Json(after));
            Rig.Reject("host_unavailable", () => host.CameraAccess().Change(s => true));
            Rig.Reject("host_unavailable", () => wall.Change(s => true));
        }
        else
        {
            Assert.Equal(before.Revision + 1, after.Revision);
            Assert.Equal(JobStatus.Cancelled, after.Jobs[0].Status);
            Assert.Equal(HiperwallSendState.Rejected, after.HiperwallDisplays[0].Targets[0].OpenState);
            Assert.Equal(DisplayCleanupState.Closed, after.HiperwallDisplays[0].Targets[0].CleanupState);
            Assert.Equal(before.Devices[0].Id, Assert.Single(after.UncertainDevices));
            Assert.Contains(after.Audit, a => a.Action == "JobCancellation");
        }
        Assert.Equal(1, fixture.Store.Attempts);
    }

    [Fact]
    public void A_domain_exception_discards_every_joined_feature_change_and_audit()
    {
        using var fixture = new BoundaryFixture();
        var before = fixture.Store.Load();
        var scenarios = fixture.Host.ScenarioAccess();
        Assert.Throws<InvalidOperationException>(() => scenarios.Change<bool>(s => {
            s.Jobs.Clear();
            fixture.Host.HiperwallAccess().For(s).HiperwallDisplays.Clear();
            scenarios.Audit(s, null, "Attempted", "rolled back");
            throw new InvalidOperationException("reject");
        }));
        Assert.Equal(Json(before), Json(fixture.Store.Load()));
        Assert.Equal(0, fixture.Store.Attempts);
    }

    [Fact]
    public void Recovery_review_uses_wall_evidence_but_ignores_retry_schedule_alone()
    {
        using var fixture = new BoundaryFixture(recovery: true);
        var host = fixture.Host; var wallAccess = host.HiperwallAccess();
        var lifecycle = new ScenarioJobLifecycle(host.ScenarioAccess(), new HiperwallJobLifecycle(wallAccess));
        var wall = new HiperwallService(wallAccess, null, null, lifecycle);
        var login = host.Login(new("admin", Rig.Password, Guid.NewGuid(), "test"));
        var review = host.ReviewRecovery(login.Token, wall.DescribeRecovery);
        Assert.Single(review.HiperwallDisplays);
        var frozen = Json(review);
        wallAccess.Change(s => { s.HiperwallDisplays[0].Targets[0].NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1); return true; });
        Assert.Equal(LeaseMode.Free, host.ApproveRecovery(login.Token, review.ReviewId, wall.DescribeRecovery).Mode);
        Assert.Equal(frozen, Json(review));
    }

    [Fact]
    public void New_wall_cleanup_evidence_requires_another_recovery_review()
    {
        using var fixture = new BoundaryFixture(recovery: true);
        var host = fixture.Host; var wallAccess = host.HiperwallAccess();
        var wall = new HiperwallService(wallAccess, null, null,
            new ScenarioJobLifecycle(host.ScenarioAccess(), new HiperwallJobLifecycle(wallAccess)));
        var login = host.Login(new("admin", Rig.Password, Guid.NewGuid(), "test"));
        var review = host.ReviewRecovery(login.Token, wall.DescribeRecovery);
        wallAccess.Change(s => { s.HiperwallDisplays[0].Targets[0].CleanupAttempts++; return true; });
        Rig.Reject("review_stale", () => host.ApproveRecovery(login.Token, review.ReviewId, wall.DescribeRecovery));
        var updated = host.ReviewRecovery(login.Token, wall.DescribeRecovery);
        Assert.Equal(1, updated.HiperwallDisplays[0].Targets[0].CleanupAttempts);
        Assert.Equal(LeaseMode.Free, host.ApproveRecovery(login.Token, updated.ReviewId, wall.DescribeRecovery).Mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_recovery_commits_lease_job_device_wall_and_camera_as_one_unit(bool fail)
    {
        using var fixture = new BoundaryFixture();
        var seed = fixture.Store.Load();
        seed.Lease = new() { Mode = LeaseMode.Held, Generation = 2, SessionId = Guid.NewGuid() };
        seed.Jobs[0].Status = JobStatus.Running;
        seed.Jobs[0].Steps[0].Status = StepStatus.Dispatching;
        seed.Cameras.Add(new(Guid.NewGuid(), 1, "camera", "room", "test-path", Guid.NewGuid(), true,
            CameraProvisioning.Ready, "ready", DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        using var store = new MemoryStore(seed) { Fail = fail };
        var host = new HostAuthority(store, new TestHasher(), new TestClock(), 15);
        var jobs = new ScenarioJobLifecycle(host.ScenarioAccess(), new HiperwallJobLifecycle(host.HiperwallAccess()));
        var wall = new HiperwallService(host.HiperwallAccess(), null, null, jobs);
        var devices = new DeviceExecutionService(host.DeviceAccess(), new DeviceDriverRegistry(new GateDriver()));
        var scenarios = new ScenarioService(host.ScenarioAccess(), devices, wall, jobs);
        var cameras = new CameraService(host.CameraAccess(), null, null, wall);
        void Recover() => host.RecoverStartup(context => {
            wall.RecoverEdits(context); scenarios.RecoverJobs(context);
            wall.RecoverHiperwallDisplays(context); cameras.RecoverCameras(context);
        });
        if (fail) Assert.Throws<IOException>(Recover); else Recover();
        Assert.Equal(1, store.Attempts);
        var saved = store.Load();
        if (fail) Assert.Equal(Json(seed), Json(saved));
        else
        {
            Assert.Equal(seed.Revision + 1, saved.Revision);
            Assert.Equal(LeaseMode.RecoveryRequired, saved.Lease.Mode);
            Assert.Equal(JobStatus.NeedsReview, saved.Jobs[0].Status);
            Assert.Equal(StepStatus.Unknown, saved.Jobs[0].Steps[0].Status);
            Assert.Equal(seed.Devices[0].Id, Assert.Single(saved.UncertainDevices));
            Assert.Equal(HiperwallSendState.Rejected, saved.HiperwallDisplays[0].Targets[0].OpenState);
            Assert.Equal(CameraProvisioning.Pending, saved.Cameras[0].Provisioning);
            Assert.Contains(saved.Audit, a => a.Action == "HostStarted");
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
    private static MediaConfiguration Media() => new(1, "http://test:9997", "http://test:8888", "api", "reader", Guid.NewGuid());
    private sealed class BoundaryFixture : IDisposable
    {
        public MemoryStore Store { get; }
        public HostAuthority Host { get; }
        public BoundaryFixture(bool recovery = false)
        {
            using var rig = new Rig();
            var device = rig.Device();
            var job = rig.SubmitScenario(rig.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1)));
            var state = rig.Store.Load();
            state.Accounts[0].DeviceIds = [device.Id];
            state.Lease = recovery ? new Lease { Mode = LeaseMode.RecoveryRequired, Generation = 3, FencedAt = rig.Clock.GetUtcNow() } : new();
            state.HiperwallDisplays.Add(new() {
                Request = new(Guid.NewGuid(), 1, 1), Requester = rig.Admin.Session, Name = "Test display", Endpoint = "http://test:80",
                Duration = new(DisplayDurationMode.Continuous), ScenarioJobId = job.Id, ScenarioStepIndex = 0,
                Targets = [new() { Command = new(HiperwallEditAction.Open, "test-instance"), OpenState = HiperwallSendState.Pending }]
            });
            Store = new(state); Host = new(Store, new TestHasher(), rig.Clock, 15);
        }
        public void Dispose() => Store.Dispose();
    }
    private sealed class MemoryStore(HostState initial) : IStateStore
    {
        private HostState _state = JsonDefaults.Copy(initial);
        public bool Fail { get; set; }
        public int Attempts { get; private set; }
        public HostState Load() => JsonDefaults.Copy(_state);
        public void Save(HostState state)
        {
            Attempts++;
            if (Fail) throw new IOException("injected failure");
            _state = JsonDefaults.Copy(state);
        }
        public void Dispose() { }
    }
}
