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
    private static async Task RunNumericInputs()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            foreach (var (model, role) in new[] { ("virtual-light", "numeric.light"), ("virtual-projector", "numeric.projector"), ("virtual-lift", "numeric.lift") })
            {
                await Execute(vm, vm.NewDeviceCommand); vm.SelectedModel = vm.Models.Single(m => m.Id == model);
                vm.DeviceName = role; vm.ConnectionId = "numeric-input-fixture";
                await Execute(vm, vm.SaveDeviceCommand);
                vm.SelectedDevice = vm.Devices.Single(d => d.Name == role); vm.RoleName = role;
                await Execute(vm, vm.SaveRoleCommand);
            }
            var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedIndex = 0;
            vm.DeviceViewIndex = 1;
            vm.SelectedRole = vm.Roles.Single(r => r.Id == "numeric.light");
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            window.UpdateLayout();
            var value = (TextBox)window.FindName("ManualNumericValue");
            var delay = (TextBox)window.FindName("ManualDelayInput");
            var timeout = (TextBox)window.FindName("ManualTimeoutInput");
            var submit = (Button)window.FindName("SubmitCommandButton");
            var error = (TextBlock)window.FindName("ManualInputErrorText");
            async Task Input(TextBox input, string text)
            {
                input.SetCurrentValue(TextBox.TextProperty, text);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            async Task Blocked(TextBox input, string text)
            {
                var count = vm.Jobs.Count;
                await Input(input, text);
                Require(!submit.IsEnabled && !vm.SubmitCommand.CanExecute(null) && error.IsVisible && error.Text.Length > 0,
                    "Invalid numeric text left submission enabled or hid the reason: " + text);
                vm.SubmitCommand.Execute(null);
                await Execute(vm, vm.RefreshCommand);
                Require(vm.Jobs.Count == count && vm.PendingSummary == "" && input.Text == text && !submit.IsEnabled,
                    "Invalid input accepted a job, created a pending request or was replaced during refresh: " + text);
            }
            foreach (var invalid in new[] { "abc", "", " ", "2147483648", "37.5", "-1", "101" })
            {
                await Input(value, "37"); await Blocked(value, invalid);
            }
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
            await Input(value, "abc"); window.UpdateLayout(); Capture(window, Path.Combine(output, "numeric-input-error.png"));
            await Input(value, "37");
            Require(submit.IsEnabled && !error.IsVisible, "Correcting to the previous valid value did not restore submission");
            await Click(vm, submit); await Wait(() => vm.Jobs.All(j => !j.Job.Active));
            Require(vm.Jobs.Count == 1 && vm.Jobs.Single().Job.Snapshot.Steps[0].Value == 37, "Corrected input was not accepted exactly once");
            foreach (var invalid in new[] { "abc", "", "2147483648", "-1", "3600001" })
            { await Input(delay, "0"); await Blocked(delay, invalid); }
            await Input(delay, "0");
            foreach (var invalid in new[] { "abc", "", "2147483648", "99", "30001" })
            { await Input(timeout, "3000"); await Blocked(timeout, invalid); }
            await Input(timeout, "3000");
            await Input(value, "100"); Require(submit.IsEnabled, "Upper brightness boundary was rejected");
            await Input(value, "0"); Require(submit.IsEnabled, "Zero brightness was rejected");

            vm.SelectedRole = vm.Roles.Single(r => r.Id == "numeric.projector");
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Input);
            await Blocked(value, "0"); await Input(value, "4"); Require(submit.IsEnabled, "Capability-specific upper boundary was rejected");
            vm.SelectedRole = vm.Roles.Single(r => r.Id == "numeric.lift");
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Lift);
            await Input(value, "-1"); Require(submit.IsEnabled, "Supported negative lift value was rejected");
            await Input(value, "abc");
            vm.SelectedRole = vm.Roles.Single(r => r.Id == "numeric.light");
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Power);
            var power = (PowerButtons)window.FindName("ManualPowerButtons"); window.UpdateLayout();
            await Click(vm, (Button)power.FindName("OnButton"));
            Require(submit.IsEnabled && vm.CommandValue == 1, "Switching from invalid numeric input broke ON/OFF selection");

            tabs.SelectedItem = window.FindName("ScenarioTab");
            SetScenarioValue(vm, "numeric.light", DeviceOperation.Brightness, 42);
            var scenarioDelay = (TextBox)window.FindName("ScenarioDelayInput");
            var scenarioTimeout = (TextBox)window.FindName("ScenarioTimeoutInput");
            var add = (Button)window.FindName("AddScenarioStep");
            var timingError = (TextBlock)window.FindName("ScenarioTimingErrorText");
            window.UpdateLayout();
            foreach (var (input, invalid) in new[] { (scenarioDelay, "abc"), (scenarioDelay, ""), (scenarioDelay, "3600001"),
                (scenarioTimeout, "abc"), (scenarioTimeout, "99"), (scenarioTimeout, "30001"), (scenarioTimeout, "2147483648") })
            {
                await Input(scenarioDelay, "0"); await Input(scenarioTimeout, "3000");
                var count = vm.ScenarioEditor.DraftSteps.Count;
                await Input(input, invalid);
                Require(!add.IsEnabled && vm.ScenarioEditor.ScenarioTimingError.Length > 0 && timingError.Text.Length > 0, "Invalid scenario timing was not blocked");
                vm.ScenarioEditor.AddStepCommand.Execute(null); await Execute(vm, vm.RefreshCommand);
                Require(vm.ScenarioEditor.DraftSteps.Count == count && input.Text == invalid, "Invalid scenario timing added a step or polling replaced the text");
            }
            await Input(scenarioDelay, "0"); await Input(scenarioTimeout, "60000");
            Require(vm.DelayMsText == "0" && vm.TimeoutMsText == "3000", "Scenario timing changed manual input");
            vm.DelayMsText = "invalid manual delay";
            Require(vm.ScenarioEditor.DelayMsText == "0" && vm.ScenarioEditor.TimeoutMsText == "60000", "Manual timing changed scenario input");
            vm.DelayMsText = "0";
            vm.ScenarioEditor.DraftStepKind = ScenarioStepKind.WaitUntil;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(add.IsEnabled && vm.ScenarioEditor.ScenarioTimingError == "", "Condition-wait timeout incorrectly used the manual 30-second maximum");
            vm.ScenarioEditor.DraftStepKind = ScenarioStepKind.DisplayLayout;
            Require(vm.ScenarioEditor.ScenarioTimingError == "", "Layout timeout incorrectly used the manual 30-second maximum");
            vm.ScenarioEditor.DraftStepKind = ScenarioStepKind.DeviceCommand;
            Require(!vm.ScenarioEditor.AddStepCommand.CanExecute(null), "Command step accepted the long wait-only timeout");
            await Input(scenarioTimeout, "3000"); await Click(vm, add);
            Require(vm.ScenarioEditor.DraftSteps.Count == 1 && vm.ScenarioEditor.DraftSteps[0].TimeoutMs == 3000 && vm.ScenarioEditor.DraftSteps[0].Value == 42,
                "Corrected scenario timing did not produce the intended step");
            vm.ScenarioEditor.ScenarioName = "숫자 입력 검증"; await Execute(vm, vm.ScenarioEditor.SaveScenarioCommand); vm.ScenarioEditor.SelectedScenario = vm.ScenarioEditor.Scenarios.Single();
            await Input(scenarioDelay, "abc"); await Input(scenarioTimeout, ""); vm.CommandValueText = "invalid manual draft";
            await Execute(vm, vm.ScenarioEditor.RunScenarioCommand); await Wait(() => vm.Jobs.Any(j => j.Job.Kind == JobKind.Scenario && !j.Job.Active));
            var job = vm.Jobs.Single(j => j.Job.Kind == JobKind.Scenario).Job;
            Require(job.Snapshot.Steps[0].Value == 42 && job.Snapshot.Steps[0].TimeoutMs == 3000 && job.Steps[0].Status == StepStatus.Simulated,
                "Saved scenario execution read unrelated invalid editor inputs");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Numeric input binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "numeric-input-result.txt"),
                "PASS: actual WPF numeric text blocks submission for letters, blank/whitespace, overflow, fractional and out-of-range values; no pending request or host job; polling preserves invalid text; correction to the same previous value restores exactly one accepted command; timing fields, capability-specific/negative values, ON/OFF, scenario timing by kind and independent saved-scenario execution. No binding warnings. Isolated virtual host, no physical devices.");
            Console.WriteLine("Numeric input validation WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
