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
            vm.DeviceSettings.SelectedModel = multiTransport; vm.DeviceSettings.DeviceTransportId = "http"; vm.DeviceSettings.SelectedModel = null; vm.DeviceSettings.SelectedModel = multiTransport;
            Require(vm.DeviceSettings.DeviceTransportId == "http", "Catalog refresh reset a supported transport choice");
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("AdminTab");
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceSettings.DeviceName = "교체 검증 전원"; vm.DeviceSettings.ConnectionId = "driver-settings-test";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.DeviceSettings.Devices.Count == 1, $"Device save failed: {vm.Message}; transport={vm.DeviceSettings.DeviceTransportId}");
            var original = vm.DeviceSettings.Devices.Single().Config;
            Require(original.DriverId == "virtual" && original.Connection.TransportId == "virtual", "Driver defaults missing");
            vm.DeviceSettings.SelectedDevice = vm.DeviceSettings.Devices.Single(); vm.DeviceSettings.RoleName = "room.power"; await Execute(vm, vm.DeviceSettings.SaveRoleCommand);
            await Execute(vm, vm.DeviceSettings.LoadDeviceCommand);
            Require(vm.DeviceSettings.DeviceDriverId == "virtual" && vm.DeviceSettings.DeviceTransports.SequenceEqual(new[] { "virtual" }) && vm.DeviceSettings.DeviceIsSimulation,
                "Settings did not use host driver catalog");
            ((Expander)window.FindName("DeviceConnectionSettings")).IsExpanded = true; window.UpdateLayout();
            var endpoint = (TextBox)window.FindName("DeviceEndpointInput");
            endpoint.Focus(); endpoint.SetCurrentValue(TextBox.TextProperty, "draft-endpoint");
            await Execute(vm, vm.RefreshCommand); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(endpoint.Text == "draft-endpoint", "Polling replaced an unsaved connection draft");
            endpoint.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.Message.Contains("가상 장비에는") && vm.DeviceSettings.Devices.Single().Config.Version == original.Version,
                "Invalid virtual endpoint was accepted");
            endpoint.SetCurrentValue(TextBox.TextProperty, ""); endpoint.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light-basic");
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.DeviceSettings.Devices.Single().Config.ModelId == "virtual-light-basic" && vm.DeviceControl.Roles.Single().DeviceId == original.Id,
                "Model replacement lost the device identity or role");
            await Execute(vm, vm.DeviceSettings.LoadDeviceCommand);
            Require(vm.DeviceSettings.DeviceEndpoint == "" && vm.DeviceSettings.DeviceDriverOptions == "{}" && vm.DeviceSettings.DeviceTransportOptions == "{}",
                "Saved settings did not round trip");
            vm.DeviceControl.SelectedRole = vm.DeviceControl.Roles.Single(); vm.DeviceControl.SelectedCapability = vm.DeviceControl.Capabilities.Single(); vm.DeviceControl.CommandValue = 1;
            await Execute(vm, vm.DeviceControl.SubmitCommand); await Wait(() => vm.JobManagement.Jobs.Count == 1 && !vm.JobManagement.Jobs[0].Job.Active);
            Require(vm.JobManagement.Jobs[0].Job.Snapshot.Steps[0].Target!.ModelId == "virtual-light-basic" &&
                vm.JobManagement.Jobs[0].Job.Steps[0].Status == StepStatus.Simulated, "Common power control did not use replacement");
            window.UpdateLayout(); Capture(window, Path.Combine(output, "device-driver-settings.png"));
            Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Driver settings binding error: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "device-driver-settings-result.txt"),
                "PASS: host catalog, settings round trip, focused draft retention, invalid transport rejection, model replacement with stable role, unchanged power control.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
