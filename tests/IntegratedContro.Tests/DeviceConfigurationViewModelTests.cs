using IntegratedContro.App;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class DeviceConfigurationViewModelTests
{
    private static (ManagementHostFake Host, DeviceSettingsViewModel Vm) Setup()
    {
        var host = new ManagementHostFake(); var d = host.State.Devices.Single();
        var pc = new PcRegistration(host.State.Session.PcId, "Registered");
        var model = new DeviceModel("fixture-model", "Model", [new(DeviceOperation.Power, 0, 1, "on/off")])
        {
            TransportIds = ["serial", "tcp"], ExecutionPcTransportIds = ["serial"], IsSimulation = false,
            Settings = [
                new("endpoint", "주소", DeviceSettingTarget.Endpoint, Required: true),
                new("port", "포트", DeviceSettingTarget.ConnectionOption, DeviceSettingKind.Integer, "2000", true, 1, 65535) { TransportId = "tcp" },
                new("address", "장비 주소", DeviceSettingTarget.Address, DeviceSettingKind.Integer, "1", true, 1, 20),
                new("channel", "채널", DeviceSettingTarget.DriverOption, DeviceSettingKind.Integer, "1", true, 1, 8),
                new("fixed", "모델 값", DeviceSettingTarget.DriverOption) { FixedValue = "fixed" }
            ]
        };
        var serial = new SharedDeviceConnection("serial-id", "Room port", 1, new("serial", "COM3") { ExecutionPcId = pc.Id });
        var tcp = new SharedDeviceConnection("tcp-id", "Room network", 1, new("tcp", "10.0.0.1") { Options = new() { ["port"] = "5000" } });
        host.Publish(host.State with { DeviceConfigurationSupported = true, Pcs = [pc], DeviceConnections = [serial, tcp],
            Models = [model, model with { Id = "pc-fixture", RequiresTargetPc = true }],
            Devices = [d with { ModelId = model.Id, ConnectionId = serial.Id, Connection = serial.Settings with { Address = "1" } }] });
        var vm = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        return (host, vm);
    }

    [Fact]
    public void Connection_first_populates_transport_settings_and_keeps_per_device_drafts()
    {
        var (host, vm) = Setup();
        vm.DeviceFields.Single(f => f.Definition.Target == DeviceSettingTarget.Address).Value = "7";
        vm.ShowAllConnections = true; vm.SelectedConnection = vm.Connections.Single(c => c.Id == "tcp-id");
        Assert.Equal("tcp", vm.DeviceTransportId); Assert.Equal("10.0.0.1", vm.DeviceEndpoint);
        Assert.Equal("5000", vm.SharedFields.Single(f => f.Definition.Key == "port").Value);
        Assert.Equal("7", vm.DeviceFields.Single(f => f.Definition.Target == DeviceSettingTarget.Address).Value);
        Assert.False(vm.CanEditSharedFields); Assert.False(vm.RequiresTargetPc);
        host.Execute(vm.EditSharedConnectionCommand); Assert.True(vm.CanEditSharedFields);
        var endpoint = vm.SharedFields.Single(f => f.Definition.Target == DeviceSettingTarget.Endpoint);
        endpoint.Value = "typed draft";
        host.Publish(JsonDefaults.Copy(host.State));
        Assert.Same(endpoint, vm.SharedFields.Single(f => f.Definition.Target == DeviceSettingTarget.Endpoint));
        Assert.Equal("typed draft", endpoint.Value);
    }

    [Fact]
    public void Transport_first_filters_connections_and_incompatible_switch_clears_selected_connection()
    {
        var (_, vm) = Setup(); vm.DeviceTransportId = "tcp";
        Assert.Equal("tcp-id", Assert.Single(vm.AvailableConnections).Id);
        vm.SelectedConnection = vm.AvailableConnections.Single(); vm.DeviceTransportId = "serial";
        Assert.Null(vm.SelectedConnection); Assert.Equal(ConnectionSaveMode.Create, vm.ConnectionMode);
        Assert.Equal("serial-id", Assert.Single(vm.AvailableConnections).Id);
        Assert.Equal("", vm.DeviceEndpoint);
    }

    [Fact]
    public void Clone_uses_new_connection_mode_and_original_shared_profile_is_unmodified()
    {
        var (host, vm) = Setup(); vm.SelectedDevice = vm.Devices.Single(); host.Execute(vm.LoadDeviceCommand);
        host.Execute(vm.CloneConnectionCommand); vm.ConnectionName = "Copy";
        vm.SharedFields.Single(f => f.Definition.Target == DeviceSettingTarget.Endpoint).Value = "COM8";
        host.Execute(vm.SaveDeviceCommand);
        var request = Assert.Single(host.DeviceSaves);
        Assert.Equal(ConnectionSaveMode.Create, request.ConnectionMode); Assert.Equal("", request.ConnectionId);
        Assert.Equal("COM8", request.Connection!.Endpoint);
        Assert.Equal("COM3", host.State.DeviceConnections.Single(c => c.Id == "serial-id").Settings.Endpoint);
    }

    [Fact]
    public void New_connection_preserves_device_fields_while_new_device_resets_them()
    {
        var (host, vm) = Setup(); vm.DeviceFields.Single(f => f.Definition.Key == "address").Value = "7";
        vm.DeviceFields.Single(f => f.Definition.Key == "channel").Value = "3";
        host.Execute(vm.NewConnectionCommand);
        Assert.Equal("7", vm.DeviceFields.Single(f => f.Definition.Key == "address").Value);
        Assert.Equal("3", vm.DeviceFields.Single(f => f.Definition.Key == "channel").Value);
        host.Execute(vm.NewDeviceCommand);
        Assert.Equal("1", vm.DeviceFields.Single(f => f.Definition.Key == "address").Value);
        Assert.Equal("1", vm.DeviceFields.Single(f => f.Definition.Key == "channel").Value);
    }

    [Fact]
    public void Required_range_and_fixed_fields_follow_driver_schema()
    {
        var (host, vm) = Setup(); vm.SelectedConnection = vm.Connections.Single(c => c.Id == "tcp-id");
        host.Execute(vm.EditSharedConnectionCommand);
        var port = vm.SharedFields.Single(f => f.Definition.Key == "port"); port.Value = "70000";
        Assert.NotEmpty(port.Error);
        Assert.True(vm.DeviceFields.Single(f => f.Definition.Key == "fixed").IsFixed);
        vm.SaveDeviceCommand.Execute(null);
        Assert.IsType<ArgumentException>(host.Error); Assert.Empty(host.DeviceSaves);
    }

    [Fact]
    public void Pc_choice_uses_registered_identity_and_survives_catalog_name_refresh_without_fallback()
    {
        var (host, vm) = Setup(); vm.SelectedModel = vm.Models.Single(m => m.RequiresTargetPc);
        vm.SelectedTargetPc = vm.Pcs.Single(); vm.ExecutionPc = vm.Pcs.Single();
        var id = vm.SelectedTargetPc.Id;
        host.Publish(host.State with { Pcs = [new(id, "Renamed"), new(Guid.NewGuid(), "Other")] });
        Assert.Equal(id, vm.SelectedTargetPc!.Id); Assert.Equal("Renamed", vm.SelectedTargetPc.Name);
        Assert.Equal(id, vm.ExecutionPc!.Id);
        vm.SelectedModel = vm.Models.First(m => !m.RequiresTargetPc); Assert.False(vm.RequiresTargetPc);
    }

    [Fact]
    public void Permission_picker_preserves_draft_and_does_not_select_new_devices_implicitly()
    {
        var (host, _) = Setup(); var vm = host.Attach(new AccountManagementViewModel(host));
        vm.SelectedDevicesOnly = true; vm.DeviceChoices.Single().IsSelected = true;
        var selected = vm.DeviceChoices.Single();
        var added = host.State.Devices[0] with { Id = Guid.NewGuid(), Name = "Later" };
        host.Publish(host.State with { Devices = [.. host.State.Devices, added], DeviceStates = new(host.State.DeviceStates) { [added.Id] = new() } });
        Assert.Same(selected, vm.DeviceChoices.Single(c => c.Id == selected.Id));
        Assert.False(vm.DeviceChoices.Single(c => c.Id == added.Id).IsSelected); Assert.False(vm.AccountAllDevices);
        Assert.Contains("제어 불가", vm.HiperwallScopeSummary);
        vm.NewAccountName = "test"; vm.ReadNewPassword = () => "fixture-password";
        host.Execute(vm.CreateAccountCommand);
        Assert.Equal(new[] { selected.Id }, host.Creations.Single().DeviceIds);
        vm.AccountAllDevices = true; Assert.Contains("허용 조건 충족", vm.HiperwallScopeSummary);
    }

    [Fact]
    public void Failed_registration_retry_retains_its_generated_identity()
    {
        var (host, vm) = Setup(); vm.SelectedConnection = vm.Connections.First(); host.Fail = true;
        vm.SaveDeviceCommand.Execute(null); vm.SaveDeviceCommand.Execute(null);
        Assert.Equal(2, host.DeviceSaves.Count); Assert.Equal(host.DeviceSaves[0].Id, host.DeviceSaves[1].Id);
        Assert.NotEqual(Guid.Empty, host.DeviceSaves[0].Id);
    }
}
