using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunDeviceDriverSettings()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext;
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            // A WPF ItemsSource refresh temporarily clears selection; preserve the chosen transport.
            var multiTransport = new DeviceModel("test-catalog", "Test catalog", [new(DeviceOperation.Power, 0, 1, "on/off")])
                { TransportIds = ["stream", "http"] };
            vm.SelectedModel = multiTransport; vm.DeviceTransportId = "http"; vm.SelectedModel = null; vm.SelectedModel = multiTransport;
            Require(vm.DeviceTransportId == "http", "Catalog refresh reset a supported transport choice");
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("AdminTab");
            vm.SelectedModel = vm.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceName = "교체 검증 전원"; vm.ConnectionId = "driver-settings-test";
            await Execute(vm, vm.SaveDeviceCommand);
            Require(vm.Devices.Count == 1, $"Device save failed: {vm.Message}; transport={vm.DeviceTransportId}");
            var original = vm.Devices.Single().Config;
            Require(original.DriverId == "virtual" && original.Connection.TransportId == "virtual", "Driver defaults missing");
            vm.SelectedDevice = vm.Devices.Single(); vm.RoleName = "room.power"; await Execute(vm, vm.SaveRoleCommand);
            await Execute(vm, vm.LoadDeviceCommand);
            Require(vm.DeviceDriverId == "virtual" && vm.DeviceTransports.SequenceEqual(new[] { "virtual" }) && vm.DeviceIsSimulation,
                "Settings did not use host driver catalog");
            ((Expander)window.FindName("DeviceConnectionSettings")).IsExpanded = true; window.UpdateLayout();
            var endpoint = (TextBox)window.FindName("DeviceEndpointInput");
            endpoint.Focus(); endpoint.SetCurrentValue(TextBox.TextProperty, "draft-endpoint");
            await Execute(vm, vm.RefreshCommand); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(endpoint.Text == "draft-endpoint", "Polling replaced an unsaved connection draft");
            endpoint.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Execute(vm, vm.SaveDeviceCommand);
            Require(vm.Message.Contains("가상 장비에는") && vm.Devices.Single().Config.Version == original.Version,
                "Invalid virtual endpoint was accepted");
            endpoint.SetCurrentValue(TextBox.TextProperty, ""); endpoint.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            vm.SelectedModel = vm.Models.Single(m => m.Id == "virtual-light-basic");
            await Execute(vm, vm.SaveDeviceCommand);
            Require(vm.Devices.Single().Config.ModelId == "virtual-light-basic" && vm.Roles.Single().DeviceId == original.Id,
                "Model replacement lost the device identity or role");
            await Execute(vm, vm.LoadDeviceCommand);
            Require(vm.DeviceEndpoint == "" && vm.DeviceDriverOptions == "{}" && vm.DeviceTransportOptions == "{}",
                "Saved settings did not round trip");
            vm.SelectedRole = vm.Roles.Single(); vm.SelectedCapability = vm.Capabilities.Single(); vm.CommandValue = 1;
            await Execute(vm, vm.SubmitCommand); await Wait(() => vm.Jobs.Count == 1 && !vm.Jobs[0].Job.Active);
            Require(vm.Jobs[0].Job.Snapshot.Steps[0].Target!.ModelId == "virtual-light-basic" &&
                vm.Jobs[0].Job.Steps[0].Status == StepStatus.Simulated, "Common power control did not use replacement");
            window.UpdateLayout(); Capture(window, Path.Combine(output, "device-driver-settings.png"));
            Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Driver settings binding error: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "device-driver-settings-result.txt"),
                "PASS: host catalog, settings round trip, focused draft retention, invalid transport rejection, model replacement with stable role, unchanged power control.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
