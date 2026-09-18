using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Tests;

public sealed class DeviceConfigurationTests
{
    private sealed class ConfigurationDriver : IDeviceDriver
    {
        public string Id => "configuration-test";
        public string Version => "1";
        public List<DeviceCommand> Sent { get; } = [];
        public DeviceModel[] Models => [Model("wide", 115200), Model("narrow", 9600), Model("pc", 115200) with { RequiresTargetPc = true }];
        private static DeviceModel Model(string id, int max) => new(id, id, [new(DeviceOperation.Power, 0, 1, "on/off")])
        {
            IsSimulation = false, TransportIds = ["serial", "tcp"], ExecutionPcTransportIds = ["serial"],
            Settings = [
                new("endpoint", "접속 주소", DeviceSettingTarget.Endpoint, Required: true),
                new("baud", "통신 속도", DeviceSettingTarget.ConnectionOption, DeviceSettingKind.Integer, "9600", true, 1200, max) { TransportId = "serial" },
                new("port", "포트", DeviceSettingTarget.ConnectionOption, DeviceSettingKind.Integer, "5000", true, 1, 65535) { TransportId = "tcp" },
                new("address", "장비 주소", DeviceSettingTarget.Address, DeviceSettingKind.Integer, "1", true, 1, 247),
                new("channel", "채널", DeviceSettingTarget.DriverOption, DeviceSettingKind.Integer, "1", true, 1, 8),
                new("protocol", "프로토콜", DeviceSettingTarget.DriverOption) { FixedValue = "v1" }
            ]
        };
        public void ValidateConfiguration(DeviceConfig device)
        {
            Assert.Equal(VirtualFault.None, device.Fault); Assert.Equal(0, device.LatencyMs);
        }
        public Task<DriverResult> ExecuteAsync(DeviceCommand command, CancellationToken cancellationToken)
        { Sent.Add(command); return Task.FromResult(new DriverResult(DriverStatus.Sent, "test only", Evidence: DeviceEvidence.Sent)); }
        public Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken cancellationToken) =>
            Task.FromResult(new DriverReading(true, new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 0 }, "test", DeviceEvidence.Observed));
    }
    private sealed class Setup : IDisposable
    {
        public Rig Storage { get; } = new();
        public ConfigurationDriver Driver { get; } = new();
        public ControlService Service { get; }
        public LoginResult Login { get; }
        public long Generation { get; }
        public PcRegistration Pc { get; }
        public StateView State => Service.GetState(Login.Token);
        public Setup()
        {
            Storage.Service.Release(Storage.Admin.Token, Storage.Generation);
            Service = new(Storage.Store, Storage.Hasher, new DeviceDriverRegistry(Driver), Storage.Clock);
            Login = Service.Login(new("admin", Rig.Password, Guid.NewGuid(), "Registered PC"));
            Generation = Service.Acquire(Login.Token).Generation;
            Pc = Service.RegisterSessionPc(Login.Token, new(Generation));
        }
        public DeviceRequest New(string name = "Device", string model = "wide") =>
            new(Generation, Guid.Empty, Guid.Empty, "ignored", name, "", model)
            {
                ConnectionMode = ConnectionSaveMode.Create, ConnectionName = "Shared port", Location = "Room A",
                Connection = new("serial", "COM7", "1") { ExecutionPcId = Pc.Id }
            };
        public DeviceConfig Save(DeviceRequest request) => Service.SaveDevice(Login.Token, request);
        public DeviceRequest Edit(DeviceConfig d, ConnectionSaveMode mode = ConnectionSaveMode.Existing) =>
            new(Generation, d.Id, d.PcId, "not trusted", d.Name, d.ConnectionId, d.ModelId, d.Enabled, ExpectedVersion: d.Version)
            {
                ConnectionMode = mode, ConnectionName = State.DeviceConnections.Single(c => c.Id == d.ConnectionId).Name,
                ExpectedConnectionVersion = State.DeviceConnections.Single(c => c.Id == d.ConnectionId).Version,
                Connection = d.Connection, DriverOptions = d.DriverOptions, Location = d.Location
            };
        public DeviceConfig Share(DeviceConfig source, string name = "Second", string model = "wide") =>
            Save(New(name, model) with { ConnectionMode = ConnectionSaveMode.Existing, ConnectionId = source.ConnectionId,
                ExpectedConnectionVersion = State.DeviceConnections.Single(c => c.Id == source.ConnectionId).Version,
                Connection = source.Connection with { Address = "2" } });
        public RoleBinding Role(DeviceConfig d) => State.Roles.Single(r => r.DeviceId == d.Id);
        public void Dispose() => Storage.Dispose();
    }

    [Fact]
    public async Task Registration_generates_identity_default_role_and_schema_defaults_and_rename_preserves_state()
    {
        using var s = new Setup(); var d = s.Save(s.New());
        Assert.NotEqual(Guid.Empty, d.Id); Assert.Equal(Guid.Empty, d.PcId); Assert.Equal("", d.PcName);
        Assert.True(s.Role(d).IsDefault); Assert.True(Guid.TryParse(s.Role(d).Id, out _));
        Assert.Equal("9600", d.Connection.Options["baud"]); Assert.Equal("1", d.DriverOptions["channel"]);
        Assert.Equal("v1", d.DriverOptions["protocol"]);
        var description = s.State.DeviceConnections.Single().Describe(s.State.Pcs, s.State.Models);
        Assert.Contains("COM7", description); Assert.Contains("Registered PC", description); Assert.Contains("통신 속도: 9600", description);
        await s.Service.ReconcileAsync(s.Login.Token, new(s.Generation, d.Id));
        var before = s.State.DeviceStates[d.Id].Observed[DeviceOperation.Power];
        var job = s.Service.Submit(s.Login.Token, new(Guid.NewGuid(), s.Generation, s.Role(d).Id));
        var renamed = s.Save(s.Edit(d) with { Name = "Renamed", Location = "Room B" });
        Assert.Equal(d.Id, renamed.Id); Assert.Equal(d.ExecutionVersion, renamed.ExecutionVersion);
        Assert.Equal(before, s.State.DeviceStates[d.Id].Observed[DeviceOperation.Power]);
        await s.Service.DispatchNextAsync();
        Assert.Equal(d.Name, s.State.Jobs.Single(j => j.Id == job.Id).Snapshot.Steps[0].Target!.Name);
        Assert.Single(s.Driver.Sent);
    }

    [Fact]
    public async Task Shared_edit_updates_all_targets_and_interrupts_admitted_snapshot_without_redirecting_it()
    {
        using var s = new Setup(); var first = s.Save(s.New()); var second = s.Share(first);
        var independent = s.Save(s.New("Independent"));
        var scenario = s.Service.SaveScenario(s.Login.Token, new(s.Generation, Guid.NewGuid(), "Shared test",
            [new(s.Role(second).Id, DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue), new(s.Role(first).Id, DeviceOperation.Power, 0)]));
        var job = s.Service.Submit(s.Login.Token, new(Guid.NewGuid(), s.Generation, null, ScenarioId: scenario.Id));
        s.Save(s.Edit(first, ConnectionSaveMode.UpdateShared) with { Connection = first.Connection with { Endpoint = "COM8" } });
        Assert.All(s.State.Devices.Where(d => d.ConnectionId == first.ConnectionId), d => Assert.Equal("COM8", d.Connection.Endpoint));
        Assert.Equal(second.ExecutionVersion + 1, s.State.Devices.Single(d => d.Id == second.Id).ExecutionVersion);
        Assert.Equal(independent.ExecutionVersion, s.State.Devices.Single(d => d.Id == independent.Id).ExecutionVersion);
        await s.Service.DispatchNextAsync();
        var stopped = s.State.Jobs.Single(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Interrupted, stopped.Status); Assert.Empty(s.Driver.Sent);
        Assert.Equal("COM7", stopped.Snapshot.Steps[0].Target!.Connection.Endpoint);
        Assert.Equal(second.Id, stopped.Snapshot.Steps[0].Target!.Id);
    }

    [Fact]
    public void Incompatible_shared_edit_is_atomic_and_per_device_address_and_options_remain_separate()
    {
        using var s = new Setup(); var first = s.Save(s.New()); var second = s.Share(first, model: "narrow");
        var before = JsonSerializer.Serialize(s.State, JsonDefaults.Options);
        Rig.Reject("invalid_setting", () => s.Save(s.Edit(first, ConnectionSaveMode.UpdateShared) with
        { Connection = first.Connection with { Options = new() { ["baud"] = "19200" } } }));
        Assert.Equal(before, JsonSerializer.Serialize(s.State, JsonDefaults.Options));
        var changed = s.Save(s.Edit(second) with { Connection = second.Connection with { Address = "3" },
            DriverOptions = new() { ["channel"] = "4" } });
        Assert.Equal("3", changed.Connection.Address); Assert.Equal("4", changed.DriverOptions["channel"]);
        Assert.Equal("1", s.State.Devices.Single(d => d.Id == first.Id).Connection.Address);
        Assert.Equal("1", s.State.Devices.Single(d => d.Id == first.Id).DriverOptions["channel"]);
    }

    [Fact]
    public void Copy_creates_distinct_connection_even_with_same_name_and_transport_and_old_connection_is_unchanged()
    {
        using var s = new Setup(); var first = s.Save(s.New()); var second = s.Share(first);
        var copy = s.Save(s.Edit(second, ConnectionSaveMode.Create) with { Connection = second.Connection with { Endpoint = "COM9" } });
        Assert.NotEqual(first.ConnectionId, copy.ConnectionId);
        Assert.Equal(2, s.State.DeviceConnections.Length);
        Assert.Single(s.State.DeviceConnections.Select(c => c.Name).Distinct());
        Assert.Equal("COM7", s.State.Devices.Single(d => d.Id == first.Id).Connection.Endpoint);
        var third = s.Save(s.New());
        Assert.NotEqual(first.ConnectionId, third.ConnectionId);
    }

    [Fact]
    public void Pc_device_uses_registered_identity_and_general_serial_device_uses_execution_pc_separately()
    {
        using var s = new Setup();
        Rig.Reject("target_required", () => s.Save(s.New(model: "pc")));
        var pcDevice = s.Save(s.New(model: "pc") with { PcId = s.Pc.Id, PcName = "Spoofed name" });
        Assert.Equal(s.Pc.Id, pcDevice.PcId); Assert.Equal(s.Pc.Name, pcDevice.PcName);
        var general = s.Save(s.New()); Assert.Equal(Guid.Empty, general.PcId); Assert.Equal(s.Pc.Id, general.Connection.ExecutionPcId);
        Rig.Reject("execution_pc_required", () => s.Save(s.New() with { Connection = new("serial", "COM1") }));
        Rig.Reject("execution_pc_missing", () => s.Save(s.New() with { Connection = new("serial", "COM1") { ExecutionPcId = Guid.NewGuid() } }));
    }

    [Fact]
    public void Shared_rename_preserves_execution_versions_and_stale_edit_is_rejected()
    {
        using var s = new Setup(); var first = s.Save(s.New()); var second = s.Share(first);
        var stale = s.Edit(second);
        var changed = s.Save(s.Edit(first, ConnectionSaveMode.UpdateShared) with { ConnectionName = "Friendly connection" });
        Assert.Equal(first.ExecutionVersion, changed.ExecutionVersion);
        Assert.Equal(second.ExecutionVersion, s.State.Devices.Single(d => d.Id == second.Id).ExecutionVersion);
        Rig.Reject("connection_changed", () => s.Save(stale));
    }

    [Fact]
    public void Storage_has_one_shared_definition_and_keeps_full_admitted_snapshots()
    {
        using var s = new Setup(); var d = s.Save(s.New()); s.Share(d);
        s.Service.Submit(s.Login.Token, new(Guid.NewGuid(), s.Generation, s.Role(d).Id));
        using var db = new SqliteConnection(s.Storage.Store.ConnectionString); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM host_state WHERE id=1";
        using var json = JsonDocument.Parse((string)cmd.ExecuteScalar()!);
        Assert.Single(json.RootElement.GetProperty("deviceConnections").EnumerateArray());
        foreach (var device in json.RootElement.GetProperty("devices").EnumerateArray())
        {
            Assert.Equal("", device.GetProperty("connection").GetProperty("endpoint").GetString());
            Assert.Empty(device.GetProperty("connection").GetProperty("options").EnumerateObject());
        }
        var loaded = s.Storage.Store.Load();
        Assert.All(loaded.Devices, device => Assert.Equal("COM7", device.Connection.Endpoint));
        Assert.Equal("COM7", loaded.Jobs.Single().Snapshot.Steps[0].Target!.Connection.Endpoint);
    }

    [Fact]
    public void Legacy_migration_preserves_pc_state_roles_scenarios_and_conflicting_connections()
    {
        var pc = Guid.NewGuid();
        DeviceConfig D(string connection, string endpoint) => new(Guid.NewGuid(), pc, "Legacy PC", "Legacy", connection, "test", 3, true, VirtualFault.ResponseLost, 120)
            { Connection = new("legacy", endpoint, "7"), DriverOptions = new() { ["channel"] = "2" } };
        var first = D("same", "A"); var second = D("same", "B"); var third = D("other", "A");
        var value = new DeviceState { Simulated = new() { [DeviceOperation.Power] = new(1, DateTimeOffset.UtcNow) } };
        var state = new HostState { Devices = [first, second, third], DeviceStates = new() { [first.Id] = value },
            Roles = [new("existing.role", first.Id, 8)], Scenarios = [new(Guid.NewGuid(), "Existing", 5, [new("existing.role", DeviceOperation.Power, 1)])] };
        DeviceConfigurationStorage.Materialize(state);
        Assert.Equal(3, state.DeviceConnections.Count); Assert.Equal("same", state.Devices[0].ConnectionId);
        Assert.NotEqual(state.Devices[0].ConnectionId, state.Devices[1].ConnectionId);
        Assert.Equal("same", state.Devices[1].LegacyConnectionId);
        Assert.All(state.Devices, d => { Assert.Equal(pc, d.PcId); Assert.Equal("Legacy PC", d.PcName); Assert.Equal(3, d.ExecutionVersion); });
        Assert.Same(value, state.DeviceStates[first.Id]); Assert.Equal("existing.role", state.Roles.Single().Id);
        var persisted = DeviceConfigurationStorage.ForStorage(state); DeviceConfigurationStorage.Materialize(persisted);
        Assert.Equal("B", persisted.Devices[1].Connection.Endpoint);
        Assert.Equal("existing.role", persisted.Scenarios.Single().Steps[0].RoleId);
    }

    [Fact]
    public void Legacy_connection_id_whitespace_cannot_create_duplicate_catalog_entries()
    {
        using var rig = new Rig(); var d = rig.Device();
        var saved = rig.Service.SaveDevice(rig.Admin.Token,
            new(rig.Generation, d.Id, d.PcId, d.PcName, "Renamed", $" {d.ConnectionId} ", d.ModelId,
                d.Enabled, d.Fault, d.LatencyMs, d.Version));
        Assert.Equal(d.ConnectionId, saved.ConnectionId);
        Assert.Single(rig.Service.GetState(rig.Admin.Token).DeviceConnections);
        Assert.Single(rig.Store.Load().DeviceConnections);
    }

    [Fact]
    public void Virtual_diagnostics_are_preserved_by_normal_edit_and_rejected_for_physical_devices()
    {
        using var rig = new Rig(); var legacy = rig.Device();
        var diagnosed = rig.Service.SaveDeviceDiagnostics(rig.Admin.Token, new(rig.Generation, legacy.Id, legacy.Version, VirtualFault.Failure, 300));
        var connection = rig.Service.GetState(rig.Admin.Token).DeviceConnections.Single(c => c.Id == legacy.ConnectionId);
        var edited = rig.Service.SaveDevice(rig.Admin.Token, new(rig.Generation, legacy.Id, Guid.Empty, "", "Renamed", legacy.ConnectionId, legacy.ModelId, ExpectedVersion: diagnosed.Version)
        { ConnectionMode = ConnectionSaveMode.Existing, ExpectedConnectionVersion = connection.Version });
        Assert.Equal(legacy.PcId, edited.PcId); Assert.Equal(legacy.PcName, edited.PcName);
        Assert.Equal(VirtualFault.Failure, edited.Fault); Assert.Equal(300, edited.LatencyMs);
        using var physical = new Setup(); var d = physical.Save(physical.New());
        Rig.Reject("simulation_required", () => physical.Service.SaveDeviceDiagnostics(physical.Login.Token,
            new(physical.Generation, d.Id, d.Version, VirtualFault.Failure, 10)));
    }

    [Fact]
    public void Default_role_can_be_inherited_without_changing_scenario_role_reference()
    {
        using var s = new Setup(); var first = s.Save(s.New()); var role = s.Role(first);
        var scenario = s.Service.SaveScenario(s.Login.Token, new(s.Generation, Guid.NewGuid(), "Existing", [new(role.Id, DeviceOperation.Power, 1)]));
        var replacement = s.Save(s.New("Replacement"));
        s.Service.SaveRole(s.Login.Token, new(s.Generation, role.Id, replacement.Id, role.Version));
        Assert.Equal(role.Id, s.State.Scenarios.Single().Steps[0].RoleId);
        Assert.Equal(scenario.Version, s.State.Scenarios.Single().Version);
        Assert.Equal(replacement.Id, s.State.Roles.Single(r => r.Id == role.Id).DeviceId);
        Assert.All(s.State.Roles.GroupBy(r => r.Id), g => Assert.Single(g));
        var assigned = s.State.Roles.Single(r => r.Id == role.Id);
        s.Service.UnassignRole(s.Login.Token, new(s.Generation, assigned.Id, replacement.Id, assigned.Version));
        Assert.Equal(assigned.Id, Assert.Single(s.State.UnassignedRoles).Id);
        s.Service.SaveRole(s.Login.Token, new(s.Generation, assigned.Id, replacement.Id, 0));
        Assert.Empty(s.State.UnassignedRoles);
        Assert.True(s.State.Roles.Single(r => r.Id == assigned.Id).Version > assigned.Version);
    }

    [Fact]
    public void Selecting_every_current_device_does_not_grant_future_devices_or_hiperwall()
    {
        using var s = new Setup(); var d = s.Save(s.New());
        var restricted = s.Service.CreateAccount(s.Login.Token, new(s.Generation, "limited", Rig.Password, AccountRole.Operator, false, [d.Id]));
        s.Service.CreateAccount(s.Login.Token, new(s.Generation, "all", Rig.Password, AccountRole.Operator, true, [d.Id]));
        var newer = s.Save(s.New("Later"));
        var limited = s.Service.Login(new("limited", Rig.Password, Guid.NewGuid(), "Client"));
        var all = s.Service.Login(new("all", Rig.Password, Guid.NewGuid(), "Client"));
        Assert.Equal(new[] { d.Id }, s.Service.GetState(limited.Token).ControllableDeviceIds);
        Assert.False(s.Service.GetState(limited.Token).CanControlHiperwall);
        Assert.Contains(newer.Id, s.Service.GetState(all.Token).ControllableDeviceIds);
        Assert.True(s.Service.GetState(all.Token).CanControlHiperwall);
        Assert.Equal(restricted.Id, s.State.Accounts.Single(a => a.Name == "limited").Id);
    }
}
