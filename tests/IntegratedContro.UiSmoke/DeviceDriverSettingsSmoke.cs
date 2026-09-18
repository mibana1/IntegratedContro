using System.Diagnostics;
using System.IO;
using System.Windows;
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
            var multiTransport = new DeviceModel("test-catalog", "Test catalog", [new(DeviceOperation.Power, 0, 1, "on/off")])
                { TransportIds = ["stream", "http"] };
            vm.DeviceSettings.SelectedModel = multiTransport; vm.DeviceSettings.DeviceTransportId = "http";
            vm.DeviceSettings.SelectedModel = null; vm.DeviceSettings.SelectedModel = multiTransport;
            Require(vm.DeviceSettings.DeviceTransportId == "http", "Catalog refresh reset transport");
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedItem = window.FindName("AdminTab");
            var view = (DeviceRegistrationView)window.FindName("DeviceRegistration");
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceSettings.DeviceName = "회의실 조명"; vm.DeviceSettings.ConnectionName = "회의실 가상 연결";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.DeviceSettings.Devices.Count == 1, vm.Message);
            var original = vm.DeviceSettings.Devices.Single().Config;
            var role = vm.DeviceControl.Roles.Single();
            Require(role.IsDefault && role.DeviceId == original.Id && original.PcId == Guid.Empty, "Automatic identity/role or PC scope incorrect");
            window.UpdateLayout();
            var connectionPicker = (ComboBox)view.FindName("ConnectionPicker");
            connectionPicker.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Execute(vm, vm.RefreshCommand);
            Require(connectionPicker.IsDropDownOpen, "Polling closed the connection list");
            connectionPicker.IsDropDownOpen = false;
            Require(!((StackPanel)view.FindName("TargetPcSettings")).IsVisible, "General device showed target PC controls");
            Require(!FindAll<TextBox>(view).Any(t => t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path is "DeviceIdText" or "PcIdText" or "DeviceTransportOptions" or "DeviceDriverOptions"),
                "Registration exposed internal IDs or JSON");
            Require(vm.DeviceSettings.SharedFields.Count == 0 && vm.DeviceSettings.DeviceFields.Count == 0, "Virtual driver exposed invented options");
            vm.DeviceSettings.SelectedDevice = vm.DeviceSettings.Devices.Single(); await Execute(vm, vm.DeviceSettings.LoadDeviceCommand);
            await Execute(vm, vm.DeviceSettings.EditSharedConnectionCommand);
            var connectionName = (TextBox)view.FindName("ConnectionNameInput");
            connectionName.Focus(); connectionName.SetCurrentValue(TextBox.TextProperty, "수정 중인 연결");
            await Execute(vm, vm.RefreshCommand); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((ComboBox)view.FindName("TransportPicker")).SelectedItem as string == "virtual", "Polling cleared the transport control");
            Require(connectionName.Text == "수정 중인 연결", "Polling replaced focused connection draft");
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.DeviceSettings.Connections.Single().Name == "수정 중인 연결", vm.Message);
            await Execute(vm, vm.DeviceSettings.NewDeviceCommand); vm.DeviceSettings.DeviceName = "복도 조명";
            vm.DeviceSettings.SelectedConnection = vm.DeviceSettings.Connections.Single();
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            var second = vm.DeviceSettings.Devices.Single(d => d.Id != original.Id);
            Require(second.Config.ConnectionId == original.ConnectionId && vm.DeviceControl.Roles.Count == 2, "Existing connection/default role not used");
            vm.DeviceSettings.SelectedDevice = second; await Execute(vm, vm.DeviceSettings.LoadDeviceCommand);
            Require(vm.DeviceSettings.ConnectionImpact.Contains("회의실 조명") && vm.DeviceSettings.ConnectionImpact.Contains("복도 조명"), "Shared impact missing");
            await Execute(vm, vm.DeviceSettings.CloneConnectionCommand); vm.DeviceSettings.ConnectionName = "복도 전용";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(second.Config.ConnectionId != original.ConnectionId && vm.DeviceSettings.Connections.Count == 2, "Copy changed the shared connection");
            tabs.SelectedItem = window.FindName("DeviceDiagnosticsTab"); window.UpdateLayout();
            await Execute(vm, vm.DeviceSettings.LoadDiagnosticsCommand);
            vm.DeviceSettings.DiagnosticFault = VirtualFault.Failure; vm.DeviceSettings.DiagnosticLatencyText = "150";
            await Execute(vm, vm.DeviceSettings.SaveDiagnosticsCommand);
            Require(second.Config.Fault == VirtualFault.Failure && second.Diagnostics.Contains("장애 주입"), "Fault state missing");
            await Execute(vm, vm.DeviceSettings.LoadDeviceCommand); vm.DeviceSettings.DeviceName = "복도 조명 변경";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(second.Config.Fault == VirtualFault.Failure && second.Config.LatencyMs == 150, "Normal edit reset diagnostics");
            await Execute(vm, vm.DeviceSettings.LoadDiagnosticsCommand); vm.DeviceSettings.DiagnosticFault = VirtualFault.None;
            await Execute(vm, vm.DeviceSettings.SaveDiagnosticsCommand);
            tabs.SelectedItem = window.FindName("AdminTab"); vm.DeviceSettings.SelectedDevice = vm.DeviceSettings.Devices.Single(d => d.Id == original.Id);
            await Execute(vm, vm.DeviceSettings.LoadDeviceCommand);
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light-basic");
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.DeviceControl.Roles.Any(r => r.Id == role.Id && r.DeviceId == original.Id), "Model change lost identity");
            vm.DeviceControl.SelectedRole = vm.DeviceControl.Roles.Single(r => r.Id == role.Id); vm.DeviceControl.CommandValue = 1;
            await Execute(vm, vm.DeviceControl.SubmitCommand); await Wait(() => vm.JobManagement.Jobs.Count == 1 && !vm.JobManagement.Jobs[0].Job.Active);
            Require(vm.JobManagement.Jobs[0].Job.Steps[0].Status == StepStatus.Simulated, "Default role power control failed");
            vm.AccountManagement.SelectedDevicesOnly = true;
            foreach (var choice in vm.AccountManagement.DeviceChoices) choice.IsSelected = true;
            Require(!vm.AccountManagement.AccountAllDevices && vm.AccountManagement.HiperwallScopeSummary.Contains("제어 불가"), "Explicit all check escalated scope");
            window.UpdateLayout(); Capture(window, Path.Combine(output, "device-driver-settings.png"));
            window.Width = 1180; window.Height = 860; view.BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "device-registration-small.png"));
            Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Configuration binding error: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "device-driver-settings-result.txt"),
                "PASS: generated identity/default role, general PC hiding, schema-only fields, focused draft retention, shared selection/edit/copy, impact names, diagnostic preservation/status, model replacement, device picker scope, no binding errors.");
            Console.WriteLine("Device configuration WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
