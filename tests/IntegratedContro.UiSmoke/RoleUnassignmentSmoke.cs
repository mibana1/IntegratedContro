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
    private static async Task RunRoleUnassignment()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext;
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            vm.SelectedModel = vm.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceName = "입구 조명"; vm.ConnectionId = "unassign-test";
            await Execute(vm, vm.SaveDeviceCommand);
            var first = vm.Devices.Single(); vm.SelectedDevice = first; vm.RoleName = "room.main";
            await Execute(vm, vm.SaveRoleCommand); vm.RoleName = "room.alias"; await Execute(vm, vm.SaveRoleCommand);
            await Execute(vm, vm.NewDeviceCommand); vm.DeviceName = "복도 조명";
            await Execute(vm, vm.SaveDeviceCommand);
            var other = vm.Devices.Single(d => d.Id != first.Id); vm.SelectedDevice = other; vm.RoleName = "room.other";
            await Execute(vm, vm.SaveRoleCommand);
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();
            ((ComboBox)window.FindName("RoleDevicePicker")).SetCurrentValue(ComboBox.SelectedItemProperty, first);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(vm.AssignedRoles.Select(r => r.Id).Order().SequenceEqual(new[] { "room.alias", "room.main" }),
                "Assignment list included another device or omitted an alias");
            var mainRow = vm.AssignedRoles.Single(r => r.Id == "room.main");
            var aliasRow = vm.AssignedRoles.Single(r => r.Id == "room.alias");
            var view = (RoleAssignmentsView)window.FindName("AssignedRoleIds");
            Button ButtonFor(RoleAssignmentRow row) => FindAll<Button>(view).Single(b => ReferenceEquals(b.DataContext, row));
            await Execute(vm, vm.RefreshCommand); await Task.Delay(1300);
            Require(ReferenceEquals(mainRow, vm.AssignedRoles.Single(r => r.Id == "room.main")), "Polling rebuilt assignment rows");
            await Execute(vm, vm.ReleaseCommand);
            Require(!ButtonFor(mainRow).IsEnabled && !ButtonFor(aliasRow).IsEnabled, "Unassignment enabled without ownership");
            await Execute(vm, vm.AcquireCommand);
            SetScenarioValue(vm, "room.main", DeviceOperation.Power, 1);
            vm.DelayMs = 60000; vm.ScenarioName = "입구 조명 순차 실행";
            await Execute(vm, vm.AddStepCommand); await Execute(vm, vm.SaveScenarioCommand);
            vm.SelectedScenario = vm.Scenarios.Single();
            Require(mainRow.Hint.Contains("입구 조명 순차 실행") && mainRow.Hint.Contains("재배정"), "Referenced definition warning missing");
            view.BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "role-unassignment.png"));
            window.Width = 1180; window.Height = 860; view.BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "role-unassignment-small.png"));

            tabs.SelectedIndex = 0; vm.DeviceViewIndex = 1; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((DataGrid)window.FindName("DeviceGrid")).ActualHeight > 80 &&
                FindAll<Button>((RoleAssignmentsView)window.FindName("QuickRoleAssignments")).Count() == 2,
                "Multiple role actions hid the compact device list");
            Require(FindAll<Button>((RoleAssignmentsView)window.FindName("QuickRoleAssignments"))
                .All(b => b.Content is TextBlock text && text.Text.Contains("· 배정 해제")), "Compact button omitted the unassign action label");
            Capture(window, Path.Combine(output, "quick-role-aliases.png"));
            tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();

            await Execute(vm, vm.RunScenarioCommand);
            Require(!ButtonFor(mainRow).IsEnabled && mainRow.Hint.Contains("진행 중"), "Active scenario did not disable used binding");
            Require(ButtonFor(aliasRow).IsEnabled, "Unrelated alias was blocked by another role's work");
            ((TextBox)window.FindName("RoleIdInput")).SetCurrentValue(TextBox.TextProperty, "draft.unrelated");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Click(vm, ButtonFor(aliasRow));
            Require(!vm.Roles.Any(r => r.Id == "room.alias") && vm.Roles.Any(r => r.Id == "room.main") &&
                vm.RoleName == "draft.unrelated" && vm.Jobs.Single().Job.Active, "Unassignment changed another binding/draft or cancelled work");
            vm.SelectedJob = vm.Jobs.Single(); await Execute(vm, vm.CancelCommand);
            Require(ButtonFor(mainRow).IsEnabled, "Completed cancellation left role locked");
            vm.SelectedRole = vm.Roles.Single(r => r.Id == "room.main");
            await Click(vm, ButtonFor(mainRow));
            Require(vm.AssignedRoles.Count == 0 && vm.Devices.Count == 2 && vm.Roles.Single().Id == "room.other" &&
                vm.Scenarios.Count == 1 && vm.SelectedScenarioTarget is null && vm.ScenarioSettings.Count == 0 &&
                vm.SelectedRole is null, "Deleted binding remained usable or device/definition was lost");
            Require(!mainRow.UnassignCommand.CanExecute(null) && !aliasRow.UnassignCommand.CanExecute(null),
                "Detached command still enabled");
            var count = vm.Jobs.Count;
            vm.SelectedScenario = vm.Scenarios.Single(); await Execute(vm, vm.RunScenarioCommand);
            Require(vm.Jobs.Count == count && vm.Message.Contains("역할을 찾을 수 없습니다"), "Definition with missing role accepted");
            vm.RoleName = "room.main"; await Execute(vm, vm.SaveRoleCommand);
            Require(vm.Roles.Single(r => r.Id == "room.main").Version > mainRow.Binding.Version &&
                !mainRow.UnassignCommand.CanExecute(null), "Recreation revived stale binding command");
            await Execute(vm, vm.RunScenarioCommand);
            Require(vm.Jobs.Count == count + 1, "Reassigned role did not restore definition execution");
            vm.SelectedJob = vm.Jobs.Single(j => j.Job.Active); await Execute(vm, vm.CancelCommand);

            tabs.SelectedIndex = 0; vm.DeviceViewIndex = 1; vm.SelectedDevice = other;
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var quick = (RoleAssignmentsView)window.FindName("QuickRoleAssignments");
            quick.BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "quick-role-unassignment.png"));
            await Click(vm, FindAll<Button>(quick).Single());
            Require(vm.Roles.Single().Id == "room.main" && vm.Devices.Count == 2 &&
                vm.RoleName == "" && vm.AssignedRoleSummary.Contains("없음"), "Quick unassignment chose another target or kept stale input");
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.Roles.Single().Id == "room.main" && vm.Devices.Count == 2, "Reconnecting restored removed assignments");
            var (observer, _) = await host.Login();
            using (observer)
            {
                var state = await HostProcess.State(observer);
                Require(state.Audit.Count(a => a.Action == "RoleUnassigned" && a.EventName == "역할 배정 해제") == 3 &&
                    state.Jobs.All(j => j.Status == JobStatus.Cancelled), "HTTP persistence/audit changed work history");
            }
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Role unassignment binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "role-unassignment-result.txt"),
                "PASS: admin and quick-view buttons; exact selected-device/alias deletion independent of typed role draft; ownership block; active/future role block until cancellation; independent alias deletion preserves running work; scenario use hint; device/definition/history preservation; missing-role execution rejection and reassign recovery; stale command disabled across delete/recreate; polling selection stability; reconnect persistence and readable audit; compact/full rendering; no binding warnings. Code-driven WPF with isolated HTTPS ControlHost, no operational device commands.");
            Console.WriteLine("Role unassignment WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
