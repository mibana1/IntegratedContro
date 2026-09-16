using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static void SetScenarioValue(MainViewModel vm, string role, DeviceOperation operation, int value)
    {
        vm.SelectedScenarioTarget = vm.ScenarioTargets.Single(t => t.Role.Id == role);
        foreach (var row in vm.ScenarioSettings) row.Included = false;
        var setting = vm.ScenarioSettings.Single(s => s.Operation == operation);
        setting.Value = value; setting.Included = true;
    }

    private static async Task RunScenarioSettings()
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
            async Task AddDevice(string model, string role, string name)
            {
                await Execute(vm, vm.NewDeviceCommand); vm.SelectedModel = vm.Models.Single(m => m.Id == model);
                vm.DeviceName = name; vm.ConnectionId = "scenario-settings"; vm.DeviceLatencyMs = 10;
                await Execute(vm, vm.SaveDeviceCommand);
                vm.SelectedDevice = vm.Devices.Single(d => d.Name == name); vm.RoleName = role;
                await Execute(vm, vm.SaveRoleCommand);
                Require(vm.ScenarioTargets.Any(t => t.Role.Id == role), "Newly registered device missing from scenarios");
            }
            await AddDevice("virtual-light", "room.light", "회의실 조명");
            await AddDevice("virtual-light-basic", "room.basic", "복도 전원 조명");
            await AddDevice("virtual-audio", "room.audio", "회의실 음향");
            await AddDevice("virtual-projector", "room.projector", "회의실 프로젝터");
            await AddDevice("virtual-lift", "room.lift", "스크린 승강");
            var audio = vm.Devices.Single(d => d.Model == "virtual-audio");
            var basic = vm.Devices.Single(d => d.Model == "virtual-light-basic");
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();
            var roleTarget = (ComboBox)window.FindName("RoleDevicePicker");
            var roleId = (TextBox)window.FindName("RoleIdInput");
            roleTarget.SetCurrentValue(ComboBox.SelectedItemProperty, audio);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(vm.RoleName == "room.audio" && roleId.Text == "room.audio" &&
                FindAll<TextBlock>((RoleAssignmentsView)window.FindName("AssignedRoleIds")).Any(t => t.Text == "room.audio"), "Assigned role ID did not populate admin fields");
            roleId.SetCurrentValue(TextBox.TextProperty, "room.audio.secondary");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Execute(vm, vm.RefreshCommand);
            Require(vm.RoleName == "room.audio.secondary", "Refresh overwrote edited role ID");
            await Execute(vm, vm.SaveRoleCommand);
            Require(vm.RoleName == "room.audio.secondary" && vm.AssignedRoleSummary.Contains("room.audio") && vm.AssignedRoleSummary.Contains("room.audio.secondary"),
                "Multiple assigned IDs not shown");
            roleId.SetCurrentValue(TextBox.TextProperty, "");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Execute(vm, vm.RefreshCommand);
            Require(vm.RoleName == "" && !vm.SaveRoleCommand.CanExecute(null), "Refresh overwrote intentionally cleared role ID");
            roleTarget.SetCurrentValue(ComboBox.SelectedItemProperty, basic);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(vm.RoleName == "room.basic", "Changing target retained another device's role ID");
            roleTarget.SetCurrentValue(ComboBox.SelectedItemProperty, audio);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FindAll<Button>(window).Single(b => b.IsVisible && ReferenceEquals(b.Command, vm.SaveRoleCommand)).BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "registered-role-ids.png"));

            tabs.SelectedItem = window.FindName("ScenarioTab"); window.UpdateLayout();
            var picker = (ComboBox)window.FindName("ScenarioTargetPicker");
            var settingsView = (ScenarioSettingsView)window.FindName("ScenarioDeviceSettings");
            var add = (Button)window.FindName("AddScenarioStep");
            void Select(string role)
            {
                picker.SetCurrentValue(ComboBox.SelectedItemProperty, vm.ScenarioTargets.Single(t => t.Role.Id == role));
                window.UpdateLayout();
            }
            await Click(vm, add);
            Require(vm.DraftSteps.Count == 0 && vm.Message.Contains("장비를 선택"), "Missing target accepted");
            Select("room.basic");
            Require(vm.ScenarioSettings.Count == 1 && vm.ScenarioSettings[0].Operation == DeviceOperation.Power &&
                !vm.ScenarioSettings[0].Included, "Power-only model exposed unsupported settings or auto-selected one");
            var off = vm.ScenarioSettings[0].Options.Single(o => o.Value == 0);
            await Click(vm, FindAll<Button>(settingsView).Single(b => ReferenceEquals(b.DataContext, off)));
            await Click(vm, add);
            Require(vm.DraftSteps.Single().Value == 0 && vm.DraftSteps.Single().ActionLabel.Contains("OFF"), "OFF button lost its numeric value or readable label");
            await Execute(vm, vm.NewScenarioCommand);
            Select("room.light");
            Require(vm.ScenarioSettings.Select(s => s.Operation).SequenceEqual(new[] { DeviceOperation.Power, DeviceOperation.Brightness }),
                "Dimmable light capability settings incorrect");
            Select("room.projector");
            Require(vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Input).Options.Select(o => o.Value).SequenceEqual(new[] { 1, 2, 3, 4 }),
                "Input options did not follow the driver's range");
            Select("room.lift");
            Require(vm.ScenarioSettings.Count == 2 && vm.ScenarioSettings[0].Options.Select(o => o.Value).SequenceEqual(new[] { 1, 0, -1 }),
                "Lift directions/STOP missing");
            vm.DraftStepKind = ScenarioStepKind.WaitUntil; window.UpdateLayout();
            Require(vm.ScenarioSettings.All(s => s.Operation != DeviceOperation.Stop), "STOP command exposed as an observable wait condition");
            vm.DraftStepKind = ScenarioStepKind.DeviceCommand;
            Select("room.audio");
            Require(vm.ScenarioSettings.Select(s => s.Operation).SequenceEqual(new[] { DeviceOperation.Power, DeviceOperation.Volume, DeviceOperation.Mute }) &&
                vm.ScenarioSettings.All(s => !s.Included), "Audio settings missing or selected without user input");
            await Click(vm, add);
            Require(vm.DraftSteps.Count == 0 && vm.Message.Contains("체크"), "Unchecked settings were appended");
            var power = vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Power);
            var volume = vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Volume);
            var mute = vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Mute);
            var on = power.Options.Single(o => o.Value == 1);
            await Click(vm, FindAll<Button>(settingsView).Single(b => ReferenceEquals(b.DataContext, on)));
            var slider = FindAll<Slider>(settingsView).Single(s => ReferenceEquals(s.DataContext, volume));
            slider.SetCurrentValue(RangeBase.ValueProperty, 37d);
            await Click(vm, FindAll<Button>(settingsView).Single(b => ReferenceEquals(b.DataContext, mute.Options.Single(o => o.Value == 0))));
            var checkbox = FindAll<CheckBox>(settingsView).Single(c => ReferenceEquals(c.DataContext, mute));
            checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!mute.Included && volume.Value == 37 && volume.Included && power.Included && on.IsSelected,
                "Buttons, slider or inclusion checkbox failed to update settings");
            await Click(vm, FindAll<Button>(settingsView).Single(b => ReferenceEquals(b.DataContext, mute.Options.Single(o => o.Value == 0))));
            vm.SelectedRole = vm.Roles.Single(r => r.Id == "room.basic"); vm.CommandValue = 0;
            await Execute(vm, vm.RefreshCommand); await Task.Delay(1300);
            Require(ReferenceEquals(volume, vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Volume)) &&
                volume.Value == 37 && power.Value == 1 && mute.Included, "Manual-control target or polling reset scenario settings");
            var input = FindAll<TextBox>(settingsView).Single(t => t.Name == "ValueInput" && ReferenceEquals(t.DataContext, volume));
            input.SetCurrentValue(TextBox.TextProperty, "101");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Click(vm, add);
            Require(vm.DraftSteps.Count == 0 && vm.Message.Contains("허용 범위"), "Invalid volume allowed partial steps");
            input.SetCurrentValue(TextBox.TextProperty, "37");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            vm.ScenarioName = "회의실 음향 켜기"; vm.DelayMs = 100; vm.TimeoutMs = 3000;
            await Click(vm, add);
            Require(vm.DraftSteps.Select(s => s.Value).SequenceEqual(new[] { 1, 37, 0 }) &&
                vm.DraftSteps.Select(s => s.DelayBeforeMs).SequenceEqual(new[] { 100, 0, 0 }) && vm.Jobs.Count == 0,
                "Grouped steps lost order/values, repeated initial delay, or sent while editing");
            await Execute(vm, vm.SaveScenarioCommand); vm.SelectedScenario = vm.Scenarios.Single();
            await Execute(vm, vm.LoadScenarioCommand);
            Require(vm.DraftSteps.Count == 3 && vm.DraftSteps[0].ActionLabel.Contains("ON") &&
                vm.DraftSteps[2].ActionLabel.Contains("음소거 해제"), "Saved definition lost readable action labels or steps");
            settingsView.BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "scenario-device-settings.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            ((ScrollViewer)window.FindName("ScenarioSettingsScroll")).ScrollToVerticalOffset(350); window.UpdateLayout();
            var location = add.TransformToAncestor(window).Transform(new Point());
            Require(location.Y + add.ActualHeight < window.ActualHeight && add.IsVisible, "Add button scrolled outside compact window");
            Capture(window, Path.Combine(output, "scenario-device-settings-small.png"));
            await Execute(vm, vm.RunScenarioCommand);
            await Wait(() => vm.Jobs.Any(j => j.Job.Status == JobStatus.Completed));
            var job = vm.Jobs.Single().Job;
            Require(job.Steps.All(s => s.Status == StepStatus.Simulated) && job.Snapshot.Steps.All(s => s.Target?.Id == audio.Id) &&
                job.Snapshot.Steps.Select(s => s.Value).SequenceEqual(new[] { 1, 37, 0 }), "Audio sequence failed or sent to another target");
            var (observer, _) = await host.Login();
            using (observer)
            {
                var state = await HostProcess.Until(observer, _ => true);
                var actual = state.DeviceStates[audio.Id].Simulated;
                Require(actual[DeviceOperation.Power].Value == 1 && actual[DeviceOperation.Volume].Value == 37 && actual[DeviceOperation.Mute].Value == 0,
                    "Virtual driver did not retain audio power/volume/mute results");
            }
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Settings binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "scenario-device-settings-result.txt"),
                "PASS: real WPF target picker, ON/OFF and mute buttons, volume slider/text, inclusion checkbox; per-model capabilities and input/direction choices; STOP excluded from waits; roles populate and preserve edits across polling; no edit-time dispatch; invalid/empty selections append nothing; grouped step order and first-only delay; save/load; actual virtual-audio power/volume/mute execution via isolated HTTPS host; compact/full renders; no binding warnings. Code-driven WPF, no physical device control.");
            Console.WriteLine("Scenario settings and role IDs WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
