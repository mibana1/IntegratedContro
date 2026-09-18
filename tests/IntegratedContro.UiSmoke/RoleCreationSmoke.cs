using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunRoleCreation()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false) { Width = 1180, Height = 860 };
        var vm = (MainViewModel)window.DataContext;
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand);
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedItem = window.FindName("AdminTab");
            var editor = (TextBox)window.FindName("NewUnassignedRoleNameEditor");
            var create = (Button)window.FindName("CreateUnassignedRoleButton");
            var adminScroll = (ScrollViewer)window.FindName("AdminSettingsScroll");
            editor.SetCurrentValue(TextBox.TextProperty, "예비 조명");
            Require(!create.IsEnabled, "Creation enabled without ownership");
            await Execute(vm, vm.AcquireCommand);
            editor.SetCurrentValue(TextBox.TextProperty, "   ");
            Require(!create.IsEnabled, "Whitespace role name enabled creation");
            editor.SetCurrentValue(TextBox.TextProperty, "  예비 조명  ");
            await Execute(vm, vm.RefreshCommand);
            Require(editor.Text == "  예비 조명  " && create.IsEnabled, "Polling lost new role name or creation required a device");
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            RequireRoleCreationVisible(adminScroll, editor, create);
            Capture(window, Path.Combine(output, "role-creation-admin.png"));
            await Click(vm, create);
            var unassigned = vm.DeviceSettings.RoleChoices.Single();
            Require(unassigned.Label.Contains("미배정") && unassigned.Binding.Name == "예비 조명" &&
                vm.DeviceSettings.ManagedRole?.Id == unassigned.Id && vm.DeviceSettings.SelectedRoleToInherit?.Id == unassigned.Id &&
                vm.DeviceSettings.Roles.Count == 0 && vm.DeviceControl.Roles.Count == 0 && vm.DeviceSettings.Devices.Count == 0 &&
                editor.Text == "" && !create.IsEnabled, "Admin role creation did not save an unassigned role or clear the input");

            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceSettings.DeviceName = "입구 조명";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            var device = vm.DeviceSettings.Devices.Single();
            tabs.SelectedIndex = 0; vm.DeviceViewIndex = 1; window.UpdateLayout();
            var quickEditor = (TextBox)window.FindName("QuickNewRoleName");
            var quickCreate = (Button)window.FindName("QuickCreateRole");
            var details = (ScrollViewer)window.FindName("DeviceDetailsScroll");
            vm.DeviceSettings.SelectedDevice = null;
            quickEditor.SetCurrentValue(TextBox.TextProperty, "야간 조명");
            Require(!quickCreate.IsEnabled, "Device role creation enabled without selected device");
            ((DataGrid)window.FindName("DeviceGrid")).SetCurrentValue(DataGrid.SelectedItemProperty, device);
            await Execute(vm, vm.RefreshCommand);
            Require(quickEditor.Text == "야간 조명" && quickCreate.IsEnabled, "Device selection or polling lost role draft");
            details.ScrollToTop(); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            RequireRoleCreationVisible(details, quickEditor, quickCreate);
            Capture(window, Path.Combine(output, "role-creation-device.png"));
            await Click(vm, quickCreate);
            var assigned = vm.DeviceSettings.Roles.Single(r => r.Name == "야간 조명");
            Require(assigned.DeviceId == device.Id && vm.DeviceSettings.AssignedRoles.Any(r => r.Id == assigned.Id) &&
                vm.DeviceControl.SelectedRole?.Id == assigned.Id && vm.ScenarioEditor.ScenarioTargets.Any(t => t.Role.Id == assigned.Id) &&
                quickEditor.Text == "" && !quickCreate.IsEnabled && vm.DeviceSettings.Roles.Count == 2,
                "Device role creation did not add/select the role or changed the existing default role");

            tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();
            ((ComboBox)window.FindName("InheritRolePicker")).SetCurrentValue(ComboBox.SelectedItemProperty,
                vm.DeviceSettings.RoleChoices.Single(r => r.Id == unassigned.Id));
            await Click(vm, (Button)window.FindName("InheritRoleButton"));
            Require(vm.DeviceSettings.Roles.Single(r => r.Id == unassigned.Id).Name == "예비 조명" &&
                vm.DeviceSettings.AssignedRoles.Count == 3, "Created admin role could not be assigned with its ID/name preserved");
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.DeviceSettings.Roles.Any(r => r.Id == assigned.Id) && vm.DeviceSettings.Roles.Any(r => r.Id == unassigned.Id),
                "Reconnect lost roles created from either screen");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Role creation binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "role-creation-result.txt"),
                "PASS: WPF name input and creation on device details and admin; visible at 1180x860; blank/ownership/target gating; polling draft preservation; admin creation without devices; automatic selection and input reset; existing role preserved; assignment and reconnect; no binding warnings. Isolated HTTPS host, virtual devices only.");
            Console.WriteLine("Role creation WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }

    private static void RequireRoleCreationVisible(ScrollViewer scroll, params FrameworkElement[] controls)
    {
        var viewport = new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight);
        foreach (var control in controls)
        {
            var bounds = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
            Require(control.IsVisible && viewport.Contains(bounds), $"Creation control {control.Name} is outside the viewport: {bounds}; viewport={viewport}");
        }
    }
}
