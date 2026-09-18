using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class ServiceSeparationTests
{
    [Fact]
    public async Task Camera_changes_and_failed_sync_do_not_change_an_in_flight_device_scenario_after_release()
    {
        var media = new PausedMedia();
        using var r = new Rig(media: media, mediaSecrets: new Secrets());
        r.Service.SaveMediaSettings(r.Admin.Token, Settings(r.Generation));
        var camera = r.Service.SaveCamera(r.Admin.Token, Camera(r.Generation));
        var target = r.Device();
        var definition = r.Scenario(new("light", DeviceOperation.Power, 1), new("light", DeviceOperation.Power, 0));
        var job = r.SubmitScenario(definition);
        var snapshot = JsonSerializer.Serialize(job.Snapshot, JsonDefaults.Options);

        var syncing = r.Service.ReconcileCamerasAsync();
        await media.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        r.Driver.Hold = true;
        var dispatching = r.Service.DispatchNextAsync();
        await r.Driver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = r.Service.SaveCamera(r.Admin.Token,
            new(r.Generation, camera.Id, camera.Version, "Changed camera", "New location", false));
        r.Service.Release(r.Admin.Token, r.Generation);

        media.Continue.SetResult();
        r.Driver.Completion.SetResult(new(DriverStatus.Simulated, "device completed",
            new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 1 }));
        await Task.WhenAll(syncing, dispatching);
        r.Driver.Hold = false;
        await r.Service.DispatchNextAsync();

        var completed = r.Job(job.Id);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Null(completed.CancelRequestedAt);
        Assert.Equal(snapshot, JsonSerializer.Serialize(completed.Snapshot, JsonDefaults.Options));
        Assert.Equal(new[] { 1, 0 }, r.Driver.Sent.Select(s => s.Value).ToArray());
        Assert.All(r.Driver.Sent, s => Assert.Equal(target.Id, s.Target!.Id));
        var state = r.Service.GetState(r.Admin.Token);
        Assert.Empty(state.UncertainDevices);
        Assert.Empty(state.HiperwallDisplayJobs);
        var saved = Assert.Single(r.Service.GetCameras(r.Admin.Token).Cameras);
        Assert.Equal(changed.Version, saved.Version);
        Assert.False(saved.Enabled);
        Assert.Equal(CameraProvisioning.Pending, saved.Provisioning); // A late old sync cannot relabel the edited camera.
    }

    [Fact]
    public async Task Rejected_camera_mapping_does_not_mutate_an_accepted_device_job()
    {
        using var r = new Rig(media: new PausedMedia(), mediaSecrets: new Secrets());
        r.Service.SaveMediaSettings(r.Admin.Token, Settings(r.Generation));
        r.Device();
        var job = r.Service.Submit(r.Admin.Token, r.Manual());
        var before = JsonSerializer.Serialize(r.Job(job.Id), JsonDefaults.Options);
        Rig.Reject("mapping_inventory_required", () => r.Service.SaveCamera(r.Admin.Token,
            Camera(r.Generation) with { ContentSelector = "uuid", ContentValue = "missing-content" }));
        Assert.Equal(before, JsonSerializer.Serialize(r.Job(job.Id), JsonDefaults.Options));
        Assert.Empty(r.Service.GetCameras(r.Admin.Token).Cameras);
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent);
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status);
    }

    [Fact]
    public async Task Camera_commit_failure_fences_every_service_through_the_same_authority()
    {
        var hasher = new TestHasher();
        using var store = new FailingStore(new HostState {
            Initialized = true, Accounts = [new InitialAdministratorPolicy(hasher).Create("admin", Rig.Password)]
        });
        var driver = new GateDriver();
        var service = new ControlService(store, hasher, driver, media: new PausedMedia(), mediaSecrets: new Secrets());
        var admin = service.Login(new("admin", Rig.Password, Guid.NewGuid(), "test-pc"));
        var generation = service.Acquire(admin.Token).Generation;
        store.Fail = true;
        Assert.Throws<IOException>(() => service.SaveMediaSettings(admin.Token, Settings(generation)));
        Rig.Reject("host_unavailable", () => service.Submit(admin.Token, new(Guid.NewGuid(), generation, "light")));
        Assert.False(await service.DispatchNextAsync());
        Assert.Empty(driver.Sent);
        Assert.Null(store.Load().Media);
    }

    [Fact]
    public async Task Camera_commit_failure_during_device_preflight_blocks_the_actual_send()
    {
        var hasher = new TestHasher();
        using var store = new FailingStore(new HostState {
            Initialized = true, Accounts = [new InitialAdministratorPolicy(hasher).Create("admin", Rig.Password)]
        });
        var driver = new GateDriver { HoldRead = true };
        var service = new ControlService(store, hasher, driver, media: new PausedMedia(), mediaSecrets: new Secrets());
        var admin = service.Login(new("admin", Rig.Password, Guid.NewGuid(), "test-pc"));
        var generation = service.Acquire(admin.Token).Generation;
        var device = service.SaveDevice(admin.Token, new(generation, Guid.NewGuid(), Guid.NewGuid(), "device-pc",
            "Light", "test-connection", "test", LatencyMs: 0));
        service.SaveRole(admin.Token, new(generation, "light", device.Id));
        var scenario = service.SaveScenario(admin.Token, new(generation, Guid.NewGuid(), "Preflight",
            [new("light", DeviceOperation.Power, 0, ConditionOperation: DeviceOperation.Power, ConditionValue: 1)]));
        service.Submit(admin.Token, new(Guid.NewGuid(), generation, null, ScenarioId: scenario.Id));
        var dispatching = service.DispatchNextAsync();
        await driver.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            store.Fail = true;
            Assert.Throws<IOException>(() => service.SaveMediaSettings(admin.Token, Settings(generation)));
        }
        finally { driver.ReadContinue.TrySetResult(); }
        Assert.False(await dispatching);
        Assert.Empty(driver.Sent);
    }


    private static SaveMediaSettingsRequest Settings(long generation) =>
        new(generation, 0, "http://localhost:9997", "http://localhost:8888", "api", "reader", "test-api", "test-reader");
    private static SaveCameraRequest Camera(long generation) =>
        new(generation, Guid.NewGuid(), 0, "Camera", "Room", true, "rtsp://camera.invalid/input");

    private sealed class Secrets : IMediaSecretStore
    {
        private readonly Dictionary<Guid, string> _values = [];
        public Guid Save(string secret) { var id = Guid.NewGuid(); _values[id] = secret; return id; }
        public string Read(Guid reference) => _values[reference];
        public void Delete(Guid reference) => _values.Remove(reference);
    }
    private sealed class PausedMedia : IMediaMtxClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task EnsurePathAsync(MediaConfiguration c, MediaCredentials credentials, string path, string source, CancellationToken ct)
        {
            Started.TrySetResult();
            await Continue.Task.WaitAsync(ct);
            throw new HttpRequestException("Test camera failure");
        }
        public Task DeletePathAsync(MediaConfiguration c, MediaCredentials credentials, string path, CancellationToken ct) => Task.CompletedTask;
        public Task<CameraConnection> GetStatusAsync(MediaConfiguration c, MediaCredentials credentials, string path, CancellationToken ct) =>
            Task.FromResult(new CameraConnection(false, "Test"));
        public Task<MediaPayload> ReadHlsAsync(MediaConfiguration c, MediaCredentials credentials, string path, string asset, CancellationToken ct) =>
            throw new NotSupportedException();
    }
    private sealed class FailingStore(HostState initial) : IStateStore
    {
        private HostState _state = JsonDefaults.Copy(initial);
        public bool Fail { get; set; }
        public HostState Load() => JsonDefaults.Copy(_state);
        public void Save(HostState state)
        {
            if (Fail) throw new IOException("Test persistence failure");
            _state = JsonDefaults.Copy(state);
        }
        public void Dispose() { }
    }
}
