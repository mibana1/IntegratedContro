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
    private static async Task RunRoleManagement()
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
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceSettings.DeviceName = "입구 조명";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            var device = vm.DeviceSettings.Devices.Single();
            vm.DeviceSettings.SelectedDevice = device;
            var roleId = vm.DeviceSettings.Roles.Single().Id;
            await Execute(vm, vm.Lighting.Lights.Single().ReadCommand);
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedItem = window.FindName("AdminTab");
            Require(((Expander)window.FindName("RoleManagementExpander")).IsExpanded, "Role management was hidden by default");
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var picker = (ComboBox)window.FindName("ManagedRolePicker");
            var editor = (TextBox)window.FindName("RoleDisplayNameEditor");
            var rename = (Button)window.FindName("RenameRoleButton");
            var delete = (Button)window.FindName("DeleteRoleButton");
            var scroll = (ScrollViewer)window.FindName("AdminSettingsScroll");
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            void RequireEditorVisible()
            {
                var viewport = new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight);
                foreach (var control in new FrameworkElement[] { picker, editor, rename, delete })
                {
                    var bounds = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
                    Require(control.IsVisible && viewport.Contains(bounds),
                        $"Role control {control.Name} is outside the visible viewport: {bounds}; viewport={viewport}");
                }
            }
            Capture(window, Path.Combine(output, "role-management-viewport.png"));
            RequireEditorVisible();
            picker.SetCurrentValue(ComboBox.SelectedItemProperty, vm.DeviceSettings.RoleChoices.Single());
            Require(editor.Text == device.Name && !delete.IsEnabled, "Assigned default role was not loaded or was deletable");
            editor.SetCurrentValue(TextBox.TextProperty, "운영 조명");
            await Execute(vm, vm.RefreshCommand);
            Require(editor.Text == "운영 조명" && rename.IsEnabled, "Polling lost role name draft");
            await Execute(vm, vm.ReleaseCommand);
            Require(!rename.IsEnabled && !delete.IsEnabled, "Management enabled without ownership");
            await Execute(vm, vm.AcquireCommand);
            await Click(vm, rename);
            Require(vm.DeviceSettings.Roles.Single().Name == "운영 조명" &&
                !vm.DeviceSettings.Roles.Single().IsDefault && !delete.IsEnabled, "Assigned role rename did not persist");
            scroll.ScrollToTop(); window.UpdateLayout(); RequireEditorVisible();
            Capture(window, Path.Combine(output, "role-management-assigned.png"));
            await Execute(vm, vm.DeviceSettings.AssignedRoles.Single().UnassignCommand);
            Require(vm.DeviceSettings.ManagedRole?.Id == roleId && delete.IsEnabled, "Unassignment did not enable eligible role deletion");
            editor.SetCurrentValue(TextBox.TextProperty, "예비 조명");
            await Click(vm, rename);
            Require(vm.DeviceSettings.RoleChoices.Single().Binding.Name == "예비 조명" && delete.IsEnabled,
                "Unassigned role rename failed");
            scroll.ScrollToTop(); window.UpdateLayout(); RequireEditorVisible();
            Capture(window, Path.Combine(output, "role-management-unassigned.png"));

            tabs.SelectedIndex = 0; vm.DeviceViewIndex = 0; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var card = vm.Lighting.Lights.Single();
            var power = FindAll<Button>(window).Single(b => b.Name == "LightPowerButton" && ReferenceEquals(b.DataContext, card));
            Require(card.RoleText == "역할 미배정" && card.Hint == "" && !power.IsEnabled, "Unassigned card lost role label or enabled control");
            var hint = FindAll<TextBlock>(power).Single(t => t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "Hint");
            Require(hint.Visibility == Visibility.Collapsed && hint.ActualHeight == 0, "Empty footer still occupied card height");
            Capture(window, Path.Combine(output, "role-management-card.png"));
            tabs.SelectedItem = window.FindName("AdminTab"); scroll.ScrollToTop(); window.UpdateLayout(); RequireEditorVisible();
            await Click(vm, delete);
            Require(vm.DeviceSettings.RoleChoices.Count == 0 && vm.DeviceSettings.ManagedRole is null &&
                vm.DeviceSettings.Devices.Count == 1 && !delete.IsEnabled, "Role deletion removed device or left stale editor");
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.DeviceSettings.RoleChoices.Count == 0 && vm.DeviceSettings.Devices.Count == 1, "Reconnect restored deleted role");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Role management binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "role-management-result.txt"),
                "PASS: role picker, editor and action buttons visible together at 1180x860; assigned and unassigned rename via WPF; assigned deletion disabled; ownership gating; polling draft preservation; unassignment then deletion; device persistence; reconnect; unassigned card footer collapsed and control disabled; no binding warnings. Isolated HTTPS host, virtual devices only.");
            Console.WriteLine("Role management WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
