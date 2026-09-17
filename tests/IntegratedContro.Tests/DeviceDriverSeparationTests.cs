using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.Tests;

public sealed class DeviceDriverSeparationTests
{
    // Deliberately different fake wire formats. Neither adapter opens a physical connection.
    private sealed class Wire
    {
        public List<(string Endpoint, string Address, string Payload)> Sent { get; } = [];
        public int Power { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Send(DeviceConfig target, string payload, int value, CancellationToken ct)
        {
            Sent.Add((target.Connection.Endpoint, target.Connection.Address, payload)); Started.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            Power = value;
        }
    }
    private sealed class VendorDriver(string id, string model, string transport, Wire wire, bool canRead = true) : IDeviceDriver
    {
        public string Id => id;
        public string Version { get; set; } = "1";
        public StepStatus Status { get; set; } = StepStatus.Observed;
        public DeviceEvidence Evidence { get; set; } = DeviceEvidence.Observed;
        public DeviceOperation Operation { get; set; } = DeviceOperation.Power;
        public DeviceModel[] Models => [new(model, id, [new(Operation, 0, 1, "on/off", canRead)], DeviceCategory.Lighting)
            { DriverId = id, IsSimulation = false, TransportIds = [transport] }];
        public void ValidateConfiguration(DeviceConfig device)
        {
            Validation.Require(device.Connection.Endpoint.StartsWith("test://", StringComparison.Ordinal) &&
                device.Connection.Address.Length > 0 && device.Fault == VirtualFault.None && device.LatencyMs == 0,
                "vendor_configuration", "Test endpoint and address required", 400);
        }
        public async Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken ct)
        {
            var payload = transport == "test-stream" ? $"PWR {step.Value}\r\n" : $"{{\"power\":{step.Value}}}";
            await wire.Send(step.Target!, payload, step.Value, ct);
            return new(Status, "Fake vendor response", new Dictionary<DeviceOperation, int> { [step.Operation] = step.Value }, Evidence);
        }
        public Task<DriverReading> ReadAsync(DeviceConfig target, CancellationToken ct) => Task.FromResult(
            new DriverReading(true, new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = wire.Power }, "Fake observation", Evidence));
    }
    private sealed class Setup : IDisposable
    {
        public Rig Storage { get; } = new();
        public Wire A { get; } = new();
        public Wire B { get; } = new();
        public VendorDriver First { get; }
        public VendorDriver Second { get; }
        public ControlService Service { get; }
        public LoginResult Login { get; }
        public long Generation { get; }
        public Setup(bool canRead = true)
        {
            First = new("vendor-a", "power-a", "test-stream", A, canRead);
            Second = new("vendor-b", "power-b", "test-http", B);
            Storage.Service.Release(Storage.Admin.Token, Storage.Generation);
            Service = new(Storage.Store, Storage.Hasher, new DeviceDriverRegistry(First, Second), Storage.Clock);
            Login = Service.Login(new("admin", Rig.Password, Guid.NewGuid(), "Test PC"));
            Generation = Service.Acquire(Login.Token).Generation;
        }
        public DeviceRequest Request(string model = "power-a", string transport = "test-stream") =>
            new(Generation, Guid.NewGuid(), Guid.NewGuid(), "Explicit PC", "Power device", "connection-a", model, LatencyMs: 0)
            { Connection = new(transport, "test://first", "1") };
        public DeviceRequest Edit(DeviceConfig d) => new(Generation, d.Id, d.PcId, d.PcName, d.Name,
            d.ConnectionId, d.ModelId, d.Enabled, d.Fault, d.LatencyMs, d.Version)
            { DriverId = d.DriverId, Connection = d.Connection, DriverOptions = d.DriverOptions };
        public DeviceConfig Register() { var d = Service.SaveDevice(Login.Token, Request()); Service.SaveRole(Login.Token, new(Generation, "room.power", d.Id)); return d; }
        public StateView State => Service.GetState(Login.Token);
        public Job Submit() => Service.Submit(Login.Token, new(Guid.NewGuid(), Generation, "room.power"));
        public void Dispose() => Storage.Dispose();
    }

    [Fact]
    public async Task Same_role_and_scenario_run_after_replacing_manufacturer_and_transport()
    {
        using var s = new Setup(); var d = s.Register();
        var definition = s.Service.SaveScenario(s.Login.Token, new(s.Generation, Guid.NewGuid(), "Power on",
            [new("room.power", DeviceOperation.Power, 1)]));
        s.Service.Submit(s.Login.Token, new(Guid.NewGuid(), s.Generation, null, ScenarioId: definition.Id));
        await s.Service.DispatchNextAsync();
        var replacement = s.Service.SaveDevice(s.Login.Token, s.Edit(d) with { ModelId = "power-b", DriverId = "vendor-b",
            Connection = new("test-http", "test://replacement", "7") });
        var role = Assert.Single(s.State.Roles);
        Assert.Equal(d.Id, role.DeviceId);
        Assert.Equal(definition.Version, Assert.Single(s.State.Scenarios).Version);
        var job = s.Service.Submit(s.Login.Token, new(Guid.NewGuid(), s.Generation, null, ScenarioId: definition.Id));
        await s.Service.DispatchNextAsync();
        Assert.Equal("PWR 1\r\n", Assert.Single(s.A.Sent).Payload);
        Assert.Equal(("test://replacement", "7", "{\"power\":1}"), Assert.Single(s.B.Sent));
        Assert.Equal("Physical", job.Snapshot.Mode);
        Assert.Equal(replacement, job.Snapshot.Steps[0].Target);
        Assert.Equal(1, s.State.DeviceStates[d.Id].Observed[DeviceOperation.Power].Value);
        Assert.Empty(s.State.DeviceStates[d.Id].Simulated);
        Assert.All(s.State.Jobs, j => Assert.Equal(JobStatus.Completed, j.Status));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("address")]
    [InlineData("transport-options")]
    [InlineData("driver-options")]
    [InlineData("replacement")]
    public async Task Accepted_work_keeps_its_snapshot_and_never_sends_to_changed_settings(string field)
    {
        using var s = new Setup(); var d = s.Register();
        var definition = s.Service.SaveScenario(s.Login.Token, new(s.Generation, Guid.NewGuid(), "Old target",
            [new("room.power", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue), new("room.power", DeviceOperation.Power, 0)]));
        var job = s.Service.Submit(s.Login.Token, new(Guid.NewGuid(), s.Generation, null, ScenarioId: definition.Id));
        var edit = s.Edit(d);
        edit = field switch
        {
            "endpoint" => edit with { Connection = d.Connection with { Endpoint = "test://other" } },
            "address" => edit with { Connection = d.Connection with { Address = "2" } },
            "transport-options" => edit with { Connection = d.Connection with { Options = new() { ["baud"] = "19200" } } },
            "driver-options" => edit with { DriverOptions = new() { ["channel"] = "2" } },
            _ => edit with { ModelId = "power-b", DriverId = "vendor-b", Connection = new("test-http", "test://other", "2") }
        };
        Assert.Equal(d.ExecutionVersion + 1, s.Service.SaveDevice(s.Login.Token, edit).ExecutionVersion);
        await s.Service.DispatchNextAsync();
        var saved = s.State.Jobs.Single(j => j.Id == job.Id);
        Assert.Equal(d, saved.Snapshot.Steps[0].Target);
        Assert.Equal(JobStatus.Interrupted, saved.Status);
        Assert.All(saved.Steps, step => Assert.Equal(StepStatus.Skipped, step.Status));
        Assert.Empty(s.A.Sent); Assert.Empty(s.B.Sent);
    }

    [Fact]
    public async Task Late_observation_cannot_be_applied_to_a_reconfigured_endpoint()
    {
        using var s = new Setup(); var d = s.Register(); s.A.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Submit(); var dispatch = s.Service.DispatchNextAsync(); await s.A.Started.Task;
        s.Service.SaveDevice(s.Login.Token, s.Edit(d) with { Connection = d.Connection with { Endpoint = "test://new" } });
        s.A.Hold.SetResult(); await dispatch;
        Assert.Empty(s.State.DeviceStates[d.Id].Observed);
        Assert.Equal(StepStatus.Observed, s.State.Jobs.Single().Steps.Single().Status);
    }

    [Theory]
    [InlineData(StepStatus.Acknowledged, DeviceEvidence.Acknowledged)]
    [InlineData(StepStatus.Sent, DeviceEvidence.Sent)]
    [InlineData(StepStatus.Acknowledged, DeviceEvidence.Observed)]
    public async Task Transmission_and_ack_do_not_turn_returned_values_into_observation(StepStatus status, DeviceEvidence evidence)
    {
        using var s = new Setup(); var d = s.Register(); s.First.Status = status; s.First.Evidence = evidence;
        s.Submit(); await s.Service.DispatchNextAsync();
        Assert.Empty(s.State.DeviceStates[d.Id].Observed); Assert.Empty(s.State.DeviceStates[d.Id].Simulated);
        Assert.Equal(1, s.State.DeviceStates[d.Id].Desired[DeviceOperation.Power]);
    }

    [Fact]
    public async Task Physical_driver_cannot_report_simulation_or_use_ack_to_satisfy_a_condition()
    {
        using var s = new Setup(); var d = s.Register(); s.First.Status = StepStatus.Simulated; s.First.Evidence = DeviceEvidence.Simulation;
        s.Submit(); await s.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Unknown, s.State.Jobs.Single().Steps.Single().Status);
        s.First.Evidence = DeviceEvidence.Acknowledged;
        Assert.Equal("read_failed", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ReconcileAsync(s.Login.Token, new(s.Generation, d.Id)))).Code);
        Assert.Contains(d.Id, s.State.UncertainDevices);
        Assert.Empty(s.State.DeviceStates[d.Id].Values);
    }

    [Fact]
    public void Unreadable_capabilities_reject_waits_and_preconditions()
    {
        using var s = new Setup(false); s.Register();
        foreach (var step in new[] { new ScenarioStep("room.power", DeviceOperation.Power, 1, Kind: ScenarioStepKind.WaitUntil),
            new ScenarioStep("room.power", DeviceOperation.Power, 1, ConditionOperation: DeviceOperation.Power, ConditionValue: 0) })
            Rig.Reject("invalid_condition", () => s.Service.SaveScenario(s.Login.Token, new(s.Generation, Guid.NewGuid(), "Invalid wait", [step])));
    }

    [Fact]
    public void Unknown_driver_model_transport_and_invalid_config_fail_without_fallback()
    {
        using var s = new Setup();
        Rig.Reject("unsupported_model", () => s.Service.SaveDevice(s.Login.Token, s.Request() with { ModelId = "unknown" }));
        Rig.Reject("unsupported_driver", () => s.Service.SaveDevice(s.Login.Token, s.Request() with { DriverId = "vendor-b" }));
        Rig.Reject("unsupported_transport", () => s.Service.SaveDevice(s.Login.Token, s.Request() with { Connection = new("virtual") }));
        Rig.Reject("vendor_configuration", () => s.Service.SaveDevice(s.Login.Token, s.Request() with { Connection = new("test-stream") }));
        Assert.Empty(s.State.Devices); Assert.Empty(s.A.Sent); Assert.Empty(s.B.Sent);
        Assert.Throws<ArgumentException>(() => new DeviceDriverRegistry(s.First, s.First));
        Assert.Throws<ArgumentException>(() => new DeviceDriverRegistry(s.First, new VendorDriver("third", "power-a", "test-http", new())));
    }

    [Fact]
    public void In_place_replacement_checks_existing_scenario_requirements_atomically()
    {
        using var s = new Setup(); var d = s.Register();
        s.Service.SaveScenario(s.Login.Token, new(s.Generation, Guid.NewGuid(), "Power required", [new("room.power", DeviceOperation.Power, 1)]));
        var third = new VendorDriver("no-power", "volume-only", "test-stream", new()) { Operation = DeviceOperation.Volume };
        s.Service.Release(s.Login.Token, s.Generation);
        var service = new ControlService(s.Storage.Store, s.Storage.Hasher, new DeviceDriverRegistry(s.First, third), s.Storage.Clock);
        var login = service.Login(new("admin", Rig.Password, Guid.NewGuid(), "PC")); var generation = service.Acquire(login.Token).Generation;
        Rig.Reject("unsupported", () => service.SaveDevice(login.Token, s.Edit(d) with { Generation = generation, DriverId = third.Id, ModelId = "volume-only" }));
        Assert.Equal(d, Assert.Single(service.GetState(login.Token).Devices));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_blocks_changed_driver_definition_and_preserves_old_manual_snapshot(bool revisionOnly)
    {
        using var s = new Setup(); s.Register(); var job = s.Submit(); s.Service.Release(s.Login.Token, s.Generation);
        var changed = new VendorDriver("vendor-a", "power-a", "test-stream", s.A, revisionOnly) { Version = revisionOnly ? "2" : "1" };
        var service = new ControlService(s.Storage.Store, s.Storage.Hasher, new DeviceDriverRegistry(changed), s.Storage.Clock);
        await service.DispatchNextAsync();
        Assert.Empty(s.A.Sent);
        var restored = s.Storage.Store.Load().Jobs.Single(j => j.Id == job.Id);
        Assert.Equal(StepStatus.Skipped, restored.Steps.Single().Status);
        Assert.True(restored.Snapshot.Steps.Single().ModelDefinition!.Capabilities.Single().CanRead);
    }

    [Fact]
    public void Mutable_request_options_and_returned_catalog_cannot_change_admitted_targets()
    {
        using var s = new Setup(); var request = s.Request() with { DriverOptions = new() { ["channel"] = "1" } };
        var d = s.Service.SaveDevice(s.Login.Token, request); s.Service.SaveRole(s.Login.Token, new(s.Generation, "room.power", d.Id));
        var job = s.Submit(); request.DriverOptions["channel"] = "99"; d.DriverOptions["channel"] = "88";
        job.Snapshot.Steps[0].Target!.Connection.Options["baud"] = "invalid";
        s.State.Models[0].Capabilities[0] = new(DeviceOperation.Volume, 0, 100, "%");
        Assert.Equal("1", s.State.Devices.Single().DriverOptions["channel"]);
        Assert.Empty(s.State.Jobs.Single().Snapshot.Steps[0].Target!.Connection.Options);
        Assert.Equal(DeviceOperation.Power, s.State.Models[0].Capabilities[0].Operation);
    }

    [Fact]
    public void Legacy_device_json_retains_virtual_defaults()
    {
        var json = JsonSerializer.Serialize(new { id = Guid.NewGuid(), pcId = Guid.NewGuid(), pcName = "PC", name = "Light",
            connectionId = "old", modelId = "virtual-light", version = 3, enabled = true, fault = "None", latencyMs = 0 });
        var device = JsonSerializer.Deserialize<DeviceConfig>(json, JsonDefaults.Options)!;
        Assert.Equal("virtual", device.DriverId); Assert.Equal("virtual", device.Connection.TransportId);
        Assert.Empty(device.DriverOptions); Assert.Equal(3, device.ExecutionVersion);
    }

    private sealed class MemoryTransport : IVirtualDeviceTransport
    {
        public Dictionary<DeviceOperation, int> Values { get; } = [];
        public Task WriteAsync(DeviceConfig target, DeviceOperation operation, int value, CancellationToken ct)
        { Values[operation] = value; return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<DeviceOperation, int>> ReadAsync(DeviceConfig target, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<DeviceOperation, int>>(Values);
    }
    [Fact]
    public async Task Virtual_protocol_can_use_a_different_transport_without_sqlite_or_service_changes()
    {
        var transport = new MemoryTransport(); var driver = new VirtualDeviceDriver(transport);
        var device = new DeviceConfig(Guid.NewGuid(), Guid.NewGuid(), "PC", "Lift", "memory", "virtual-lift", 1, true, VirtualFault.None, 0);
        await driver.ExecuteAsync(new(null, device, DeviceOperation.Stop, 0, "STOP", 0, 100, FailurePolicy.Stop, null, null), default);
        Assert.Equal(0, Assert.Single(transport.Values).Value);
        Assert.Equal(DeviceOperation.Lift, Assert.Single(transport.Values).Key);
        Assert.DoesNotContain(DeviceOperation.Stop, (await driver.ReadAsync(device, default)).Values.Keys);
    }
}
