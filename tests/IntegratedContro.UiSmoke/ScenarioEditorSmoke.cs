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
    private static async Task RunScenarioEditor()
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
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceSettings.DeviceName = "순서 검증 조명"; vm.DeviceSettings.ConnectionId = "scenario-editor-smoke";
            await Execute(vm, vm.DeviceSettings.SaveDeviceCommand); vm.DeviceSettings.SelectedDevice = vm.DeviceSettings.Devices.Single(); vm.DeviceSettings.RoleName = "light";
            await Execute(vm, vm.DeviceSettings.SaveRoleCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("ScenarioTab"); window.UpdateLayout();
            var grid = (DataGrid)window.FindName("ScenarioStepGrid");
            Button Button(string name) => (Button)window.FindName(name);
            var first = Button("MoveScenarioStepFirst"); var up = Button("MoveScenarioStepUp");
            var down = Button("MoveScenarioStepDown"); var last = Button("MoveScenarioStepLast");
            var remove = Button("DeleteScenarioStep"); var delete = Button("DeleteScenarioDefinition");
            var moves = new[] { first, up, down, last };
            async Task Select(int index)
            {
                grid.SetCurrentValue(DataGrid.SelectedIndexProperty, index);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(vm.ScenarioEditor.SelectedDraftStepIndex == index, "Selected row index binding failed");
            }
            Require(moves.All(b => !b.IsEnabled) && !remove.IsEnabled && !delete.IsEnabled, "Empty editor allowed movement/deletion");
            var a = new ScenarioStep("light", DeviceOperation.Power, 1);
            var b = new ScenarioStep("light", DeviceOperation.Brightness, 30, 0, 2500, FailurePolicy.Continue);
            var c = new ScenarioStep("light", DeviceOperation.Power, 1, 0, 4000, Kind: ScenarioStepKind.WaitUntil);
            var duplicate = a with { }; var e = new ScenarioStep("light", DeviceOperation.Power, 0);
            foreach (var step in new[] { a, b, c, duplicate, e }) vm.ScenarioEditor.DraftSteps.Add(step);
            await Select(3); await Click(vm, up);
            Require(ReferenceEquals(vm.ScenarioEditor.DraftSteps[2], duplicate) && ReferenceEquals(vm.ScenarioEditor.DraftSteps[0], a) && grid.SelectedIndex == 2,
                "Up moved an equal earlier step or lost selection");
            await Click(vm, down);
            Require(ReferenceEquals(vm.ScenarioEditor.DraftSteps[3], duplicate) && grid.SelectedIndex == 3, "Down lost selected step");
            await Click(vm, first);
            Require(ReferenceEquals(vm.ScenarioEditor.DraftSteps[0], duplicate) && grid.SelectedIndex == 0 && !first.IsEnabled && !up.IsEnabled,
                "First move/boundary failed");
            await Click(vm, last);
            Require(ReferenceEquals(vm.ScenarioEditor.DraftSteps[^1], duplicate) && grid.SelectedIndex == 4 && !last.IsEnabled && !down.IsEnabled,
                "Last move/boundary failed");
            await Execute(vm, vm.RefreshCommand);
            Require(grid.SelectedIndex == 4 && ReferenceEquals(vm.ScenarioEditor.DraftSteps[4], duplicate), "Polling lost selected duplicate");
            await Click(vm, remove);
            Require(vm.ScenarioEditor.DraftSteps.Count == 4 && ReferenceEquals(vm.ScenarioEditor.DraftSteps[0], a) && ReferenceEquals(vm.ScenarioEditor.DraftSteps[3], e) &&
                grid.SelectedIndex == 3, "Deleting duplicate removed earlier equal row");
            await Click(vm, first); // OFF → ON → brightness → wait for ON.
            var expected = new[] { e, a, b, c };
            Require(vm.ScenarioEditor.DraftSteps.SequenceEqual(expected) && !grid.CanUserSortColumns, "Visible order differs from execution order");
            await Execute(vm, vm.ReleaseCommand);
            Require(!delete.IsEnabled && down.IsEnabled, "Local ordering incorrectly requires lease");
            await Click(vm, down); await Click(vm, up);
            await Execute(vm, vm.AcquireCommand);
            vm.ScenarioEditor.ScenarioName = "순서 변경 검증"; await Click(vm, Button("SaveScenarioDefinition"));
            var saved = vm.ScenarioEditor.Scenarios.Single(); vm.ScenarioEditor.SelectedScenario = saved;
            Require(saved.Steps.SequenceEqual(expected), "Save changed order/step settings");
            await Execute(vm, vm.ScenarioEditor.LoadScenarioCommand);
            Require(vm.ScenarioEditor.DraftSteps.SequenceEqual(expected), "Load changed order/step settings");
            await Select(2);
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "scenario-editor-small.png"));
            Require(moves.All(button => button.IsVisible && button.ActualWidth > 0) && grid.ActualHeight > 100,
                "Compact layout hid movement controls or steps");
            await Execute(vm, vm.ScenarioEditor.RunScenarioCommand); await Wait(() => vm.JobManagement.Jobs.Any(j => j.Job.Status == JobStatus.Completed));
            var completed = vm.JobManagement.Jobs.Single().Job;
            Require(completed.Snapshot.Steps.Select(s => (s.Kind, s.Operation, s.Value))
                .SequenceEqual(expected.Select(s => (s.Kind, s.Operation, s.Value))), "Execution ignored saved order");
            Require(completed.Steps.Select(s => s.Status).SequenceEqual(new[] {
                StepStatus.Simulated, StepStatus.Simulated, StepStatus.Simulated, StepStatus.ConditionMet }), "Ordered execution failed");

            await Execute(vm, vm.ScenarioEditor.NewScenarioCommand); vm.ScenarioEditor.ScenarioName = "별도 삭제 대상";
            vm.ScenarioEditor.DraftSteps.Add(a); await Execute(vm, vm.ScenarioEditor.SaveScenarioCommand);
            var other = vm.ScenarioEditor.Scenarios.Single(s => s.Id != saved.Id);
            vm.ScenarioEditor.SelectedScenario = saved; await Execute(vm, vm.ScenarioEditor.LoadScenarioCommand); await Select(1);
            vm.ScenarioEditor.SelectedScenario = other; await Click(vm, delete);
            Require(vm.ScenarioEditor.Scenarios.Single().Id == saved.Id && vm.ScenarioEditor.ScenarioName == saved.Name &&
                vm.ScenarioEditor.DraftSteps.SequenceEqual(expected) && grid.SelectedIndex == 1, "Deleting selected definition erased another editor");
            vm.ScenarioEditor.SelectedScenario = vm.ScenarioEditor.Scenarios.Single();
            vm.ScenarioEditor.DraftSteps[0] = vm.ScenarioEditor.DraftSteps[0] with { DelayBeforeMs = 60000 };
            await Execute(vm, vm.ScenarioEditor.SaveScenarioCommand); await Execute(vm, vm.ScenarioEditor.RunScenarioCommand);
            Require(!delete.IsEnabled && vm.ScenarioEditor.ScenarioDeletionHint.Contains("진행 중"), "Active scenario allowed deletion");
            vm.JobManagement.SelectedJob = vm.JobManagement.Jobs.Single(j => j.Job.Active); await Execute(vm, vm.JobManagement.CancelCommand);
            Require(delete.IsEnabled, "Cancellation left definition locked");
            await Execute(vm, vm.ReleaseCommand);
            Require(!delete.IsEnabled && vm.ScenarioEditor.ScenarioDeletionHint.Contains("사용"), "Deletion enabled without ownership");
            await Execute(vm, vm.AcquireCommand); await Click(vm, delete);
            Require(vm.ScenarioEditor.Scenarios.Count == 0 && vm.ScenarioEditor.DraftSteps.Count == 0 && vm.ScenarioEditor.ScenarioName == "" && !delete.IsEnabled &&
                vm.JobManagement.Jobs.Any(j => j.Id == completed.Id && j.Job.Status == JobStatus.Completed), "Deletion left stale editor or lost history");
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.ScenarioEditor.Scenarios.Count == 0 && vm.JobManagement.Jobs.Count == 2, "Reconnect restored deleted definition or lost history");

            // Single-step boundaries and 100-step endpoint selection.
            vm.ScenarioEditor.DraftSteps.Add(a); await Select(0);
            Require(moves.All(button => !button.IsEnabled) && remove.IsEnabled, "Single-step movement enabled");
            await Click(vm, remove); Require(grid.SelectedIndex == -1 && !remove.IsEnabled, "Last deletion kept invalid selection");
            for (var i = 0; i < 100; i++) vm.ScenarioEditor.DraftSteps.Add(b with { Value = i });
            await Select(99); var lastStep = vm.ScenarioEditor.DraftSteps[99]; await Click(vm, first);
            Require(ReferenceEquals(vm.ScenarioEditor.DraftSteps[0], lastStep) && grid.SelectedIndex == 0, "100-step first move failed");
            await Click(vm, last);
            Require(ReferenceEquals(vm.ScenarioEditor.DraftSteps[99], lastStep) && grid.SelectedIndex == 99, "100-step last move failed");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Scenario editor binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "scenario-editor-result.txt"),
                "PASS: four real WPF move buttons, index selection with equal duplicate steps, precise deletion, boundaries and 100 steps, polling retention, local editing without lease, saved/load/executed order including wait and settings, selected definition deletion independent of draft, active/ownership guards, completed history and reconnect persistence, compact layout, no binding warnings. Isolated HTTPS host; no operational commands.");
            Console.WriteLine("Scenario editor WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
