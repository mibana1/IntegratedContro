using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunPowerInputs()
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
            foreach (var (model, role, name) in new[] { ("virtual-light", "light", "버튼 검증 조명"),
                ("virtual-projector", "projector", "버튼 검증 프로젝터"), ("virtual-lift", "lift", "승강 검증") })
            {
                await Execute(vm, vm.DeviceSettings.NewDeviceCommand); vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == model);
                vm.DeviceSettings.DeviceName = name; vm.DeviceSettings.ConnectionId = "power-input-smoke";
                await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
                vm.DeviceSettings.SelectedDevice = vm.DeviceSettings.Devices.Single(d => d.Name == name); vm.DeviceSettings.RoleName = role;
                await Execute(vm, vm.DeviceSettings.SaveRoleCommand);
            }
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedIndex = 0; vm.DeviceViewIndex = 1; window.UpdateLayout();
            var rolePicker = (ComboBox)window.FindName("ControlRolePicker");
            rolePicker.SetCurrentValue(ComboBox.SelectedItemProperty, vm.DeviceControl.Roles.Single(r => r.Id == "light"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var power = (PowerButtons)window.FindName("ManualPowerButtons");
            var numeric = (TextBox)window.FindName("ManualNumericValue");
            var on = (Button)power.FindName("OnButton"); var off = (Button)power.FindName("OffButton");
            Require(power.IsVisible && !numeric.IsVisible && vm.DeviceControl.IsPowerCommand, "Power retained numeric editor");
            await Click(vm, on);
            Require(vm.DeviceControl.CommandValue == 1 && vm.JobManagement.Jobs.Count == 0 &&
                ((SolidColorBrush)on.Background).Color == Color.FromRgb(0x15, 0x6D, 0x68), "ON choice lost value/highlight or dispatched early");
            await Execute(vm, vm.DeviceControl.SubmitCommand); await Wait(() => vm.JobManagement.Jobs.All(j => !j.Job.Active));
            Require(vm.JobManagement.Jobs.Single().Job.Snapshot.Steps[0].Value == 1, "ON button did not submit numeric 1");
            await Click(vm, off); await Execute(vm, vm.RefreshCommand); await Task.Delay(1300);
            Require(vm.DeviceControl.CommandValue == 0 && power.Value == 0 &&
                ((SolidColorBrush)off.Background).Color == Color.FromRgb(0x15, 0x6D, 0x68), "OFF choice lost value/highlight during refresh");
            await Execute(vm, vm.DeviceControl.SubmitCommand); await Wait(() => vm.JobManagement.Jobs.All(j => !j.Job.Active));
            Require(vm.JobManagement.Jobs.First().Job.Snapshot.Steps[0].Value == 0, "OFF button did not submit numeric 0");
            var capability = (ComboBox)window.FindName("ControlCapabilityPicker");
            capability.SetCurrentValue(ComboBox.SelectedItemProperty, vm.DeviceControl.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!power.IsVisible && numeric.IsVisible, "Brightness did not restore numeric input");
            numeric.SetCurrentValue(TextBox.TextProperty, "42");
            await Execute(vm, vm.RefreshCommand); Require(vm.DeviceControl.CommandValue == 42, "Non-power draft lost during refresh");
            capability.SetCurrentValue(ComboBox.SelectedItemProperty, vm.DeviceControl.Capabilities.Single(c => c.Operation == DeviceOperation.Power));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Click(vm, on); await Execute(vm, vm.DeviceControl.SubmitCommand); await Wait(() => vm.JobManagement.Jobs.All(j => !j.Job.Active));
            rolePicker.SetCurrentValue(ComboBox.SelectedItemProperty, vm.DeviceControl.Roles.Single(r => r.Id == "projector"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(power.IsVisible && !numeric.IsVisible, "Another power-capable model retained numeric input");
            await Click(vm, on); await Execute(vm, vm.RefreshCommand);
            Require(vm.DeviceControl.CommandValue == 1 && vm.DeviceControl.SelectedRole?.Id == "projector", "Other target's power choice changed");
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "manual-power-buttons.png"));

            tabs.SelectedItem = window.FindName("ScenarioTab");
            SetScenarioValue(vm, "light", DeviceOperation.Brightness, 65);
            vm.ScenarioEditor.DelayMs = 0; vm.ScenarioEditor.TimeoutMs = 3000; vm.ScenarioEditor.ScenarioName = "전원 조건 버튼 검증";
            ((Expander)window.FindName("ScenarioConditionExpander")).IsExpanded = true; window.UpdateLayout();
            var conditionKind = (ComboBox)window.FindName("ConditionOperationPicker");
            var conditionPower = (PowerButtons)window.FindName("ConditionPowerButtons");
            var conditionNumber = (TextBox)window.FindName("ConditionNumericValue");
            Require(!conditionPower.IsVisible && !conditionNumber.IsVisible, "No-condition option displayed a value input");
            conditionKind.SetCurrentValue(ComboBox.SelectedValueProperty, "Power");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(conditionPower.IsVisible && !conditionNumber.IsVisible, "Power precondition retained numeric input");
            await Click(vm, (Button)conditionPower.FindName("OnButton"));
            await Execute(vm, vm.RefreshCommand);
            Require(vm.ScenarioEditor.ConditionValueText == "1" && vm.DeviceControl.CommandValue == 1, "Condition button or polling changed another editor");
            var before = vm.JobManagement.Jobs.Count;
            await Execute(vm, vm.ScenarioEditor.AddStepCommand);
            SetScenarioValue(vm, "light", DeviceOperation.Power, 0); await Execute(vm, vm.ScenarioEditor.AddStepCommand);
            SetScenarioValue(vm, "light", DeviceOperation.Brightness, 20);
            await Click(vm, (Button)conditionPower.FindName("OffButton"));
            Require(vm.DeviceControl.CommandValue == 1 && vm.ScenarioEditor.ConditionValueText == "0", "Condition and manual ON/OFF controls shared draft values");
            await Execute(vm, vm.ScenarioEditor.AddStepCommand);
            Require(vm.JobManagement.Jobs.Count == before && vm.ScenarioEditor.DraftSteps.Select(s => s.ConditionValue).SequenceEqual(new int?[] { 1, 1, 0 }) &&
                vm.ScenarioEditor.DraftSteps.All(s => s.ConditionOperation == DeviceOperation.Power), "Condition buttons lost step expectations or sent while editing");
            await Execute(vm, vm.ScenarioEditor.SaveScenarioCommand); vm.ScenarioEditor.SelectedScenario = vm.ScenarioEditor.Scenarios.Single();
            await Execute(vm, vm.ScenarioEditor.LoadScenarioCommand);
            Require(vm.ScenarioEditor.DraftSteps.Select(s => s.ConditionValue).SequenceEqual(new int?[] { 1, 1, 0 }), "Saved ON/OFF preconditions changed");
            conditionPower.BringIntoView(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "scenario-power-condition.png"));
            await Execute(vm, vm.ScenarioEditor.RunScenarioCommand);
            await Wait(() => vm.JobManagement.Jobs.Any(j => j.Job.Kind == JobKind.Scenario && !j.Job.Active));
            Require(vm.JobManagement.Jobs.Single(j => j.Job.Kind == JobKind.Scenario).Job.Steps.All(s => s.Status == StepStatus.Simulated),
                "Actual virtual ON/OFF preconditions did not match button choices");

            conditionKind.SetCurrentValue(ComboBox.SelectedValueProperty, "Brightness");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!conditionPower.IsVisible && conditionNumber.IsVisible, "Numeric precondition did not restore editor");
            conditionNumber.SetCurrentValue(TextBox.TextProperty, "20");
            await Execute(vm, vm.RefreshCommand);
            Require(vm.ScenarioEditor.ConditionValueText == "20", "Condition numeric input lost across polling");
            vm.ScenarioEditor.SelectedScenarioTarget = vm.ScenarioEditor.ScenarioTargets.Single(t => t.Role.Id == "lift");
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(vm.ScenarioEditor.ConditionOperationText == "" && vm.ScenarioEditor.ConditionValueText == "" &&
                vm.ScenarioEditor.ConditionOperations.All(c => c.Value != "Power"), "Changing target carried an unsupported power condition");

            vm.ScenarioEditor.SelectedScenarioTarget = vm.ScenarioEditor.ScenarioTargets.Single(t => t.Role.Id == "light");
            window.UpdateLayout();
            var settings = (ScenarioSettingsView)window.FindName("ScenarioDeviceSettings");
            var brightness = vm.ScenarioEditor.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Brightness);
            var slider = FindAll<Slider>(settings).Single(s => ReferenceEquals(s.DataContext, brightness));
            slider.BringIntoView(); window.UpdateLayout();
            Require(slider.IsMoveToPointEnabled && slider.IsSnapToTickEnabled &&
                slider.Template.FindName("PART_Track", slider) is Track, "Scenario slider lacks WPF direct-position track behavior");
            slider.SetCurrentValue(RangeBase.ValueProperty, 87d);
            Require(brightness.Value == 87 && brightness.Included, "Slider value did not immediately update the included setting");
            await Execute(vm, vm.RefreshCommand);
            Require(brightness.Value == 87, "Polling moved the brightness thumb");
            Capture(window, Path.Combine(output, "scenario-brightness-direct.png"));
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Power input binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "power-input-result.txt"),
                "PASS: actual manual ON/OFF button choices and numeric 1/0 dispatch on isolated virtual host; highlight and polling preservation; brightness numeric editor and power-capable model switching; optional condition capability selector, independent ON/OFF buttons, save/load and virtual condition execution; target-change reset; WPF IsMoveToPointEnabled stock track and immediate slider binding; compact screenshots; no binding warnings. Code-driven WPF, no physical pointer or device verification.");
            Console.WriteLine("Power buttons and direct-position slider WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
