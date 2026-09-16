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
            vm.SelectedModel = vm.Models.Single(m => m.Id == "virtual-light");
            vm.DeviceName = "순서 검증 조명"; vm.ConnectionId = "scenario-editor-smoke";
            await Execute(vm, vm.SaveDeviceCommand); vm.SelectedDevice = vm.Devices.Single(); vm.RoleName = "light";
            await Execute(vm, vm.SaveRoleCommand);
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
                Require(vm.SelectedDraftStepIndex == index, "Selected row index binding failed");
            }
            Require(moves.All(b => !b.IsEnabled) && !remove.IsEnabled && !delete.IsEnabled, "Empty editor allowed movement/deletion");
            var a = new ScenarioStep("light", DeviceOperation.Power, 1);
            var b = new ScenarioStep("light", DeviceOperation.Brightness, 30, 0, 2500, FailurePolicy.Continue);
            var c = new ScenarioStep("light", DeviceOperation.Power, 1, 0, 4000, Kind: ScenarioStepKind.WaitUntil);
            var duplicate = a with { }; var e = new ScenarioStep("light", DeviceOperation.Power, 0);
            foreach (var step in new[] { a, b, c, duplicate, e }) vm.DraftSteps.Add(step);
            await Select(3); await Click(vm, up);
            Require(ReferenceEquals(vm.DraftSteps[2], duplicate) && ReferenceEquals(vm.DraftSteps[0], a) && grid.SelectedIndex == 2,
                "Up moved an equal earlier step or lost selection");
            await Click(vm, down);
            Require(ReferenceEquals(vm.DraftSteps[3], duplicate) && grid.SelectedIndex == 3, "Down lost selected step");
            await Click(vm, first);
            Require(ReferenceEquals(vm.DraftSteps[0], duplicate) && grid.SelectedIndex == 0 && !first.IsEnabled && !up.IsEnabled,
                "First move/boundary failed");
            await Click(vm, last);
            Require(ReferenceEquals(vm.DraftSteps[^1], duplicate) && grid.SelectedIndex == 4 && !last.IsEnabled && !down.IsEnabled,
                "Last move/boundary failed");
            await Execute(vm, vm.RefreshCommand);
            Require(grid.SelectedIndex == 4 && ReferenceEquals(vm.DraftSteps[4], duplicate), "Polling lost selected duplicate");
            await Click(vm, remove);
            Require(vm.DraftSteps.Count == 4 && ReferenceEquals(vm.DraftSteps[0], a) && ReferenceEquals(vm.DraftSteps[3], e) &&
                grid.SelectedIndex == 3, "Deleting duplicate removed earlier equal row");
            await Click(vm, first); // OFF → ON → brightness → wait for ON.
            var expected = new[] { e, a, b, c };
            Require(vm.DraftSteps.SequenceEqual(expected) && !grid.CanUserSortColumns, "Visible order differs from execution order");
            await Execute(vm, vm.ReleaseCommand);
            Require(!delete.IsEnabled && down.IsEnabled, "Local ordering incorrectly requires lease");
            await Click(vm, down); await Click(vm, up);
            await Execute(vm, vm.AcquireCommand);
            vm.ScenarioName = "순서 변경 검증"; await Click(vm, Button("SaveScenarioDefinition"));
            var saved = vm.Scenarios.Single(); vm.SelectedScenario = saved;
            Require(saved.Steps.SequenceEqual(expected), "Save changed order/step settings");
            await Execute(vm, vm.LoadScenarioCommand);
            Require(vm.DraftSteps.SequenceEqual(expected), "Load changed order/step settings");
            await Select(2);
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "scenario-editor-small.png"));
            Require(moves.All(button => button.IsVisible && button.ActualWidth > 0) && grid.ActualHeight > 100,
                "Compact layout hid movement controls or steps");
            await Execute(vm, vm.RunScenarioCommand); await Wait(() => vm.Jobs.Any(j => j.Job.Status == JobStatus.Completed));
            var completed = vm.Jobs.Single().Job;
            Require(completed.Snapshot.Steps.Select(s => (s.Kind, s.Operation, s.Value))
                .SequenceEqual(expected.Select(s => (s.Kind, s.Operation, s.Value))), "Execution ignored saved order");
            Require(completed.Steps.Select(s => s.Status).SequenceEqual(new[] {
                StepStatus.Simulated, StepStatus.Simulated, StepStatus.Simulated, StepStatus.ConditionMet }), "Ordered execution failed");

            await Execute(vm, vm.NewScenarioCommand); vm.ScenarioName = "별도 삭제 대상";
            vm.DraftSteps.Add(a); await Execute(vm, vm.SaveScenarioCommand);
            var other = vm.Scenarios.Single(s => s.Id != saved.Id);
            vm.SelectedScenario = saved; await Execute(vm, vm.LoadScenarioCommand); await Select(1);
            vm.SelectedScenario = other; await Click(vm, delete);
            Require(vm.Scenarios.Single().Id == saved.Id && vm.ScenarioName == saved.Name &&
                vm.DraftSteps.SequenceEqual(expected) && grid.SelectedIndex == 1, "Deleting selected definition erased another editor");
            vm.SelectedScenario = vm.Scenarios.Single();
            vm.DraftSteps[0] = vm.DraftSteps[0] with { DelayBeforeMs = 60000 };
            await Execute(vm, vm.SaveScenarioCommand); await Execute(vm, vm.RunScenarioCommand);
            Require(!delete.IsEnabled && vm.ScenarioDeletionHint.Contains("진행 중"), "Active scenario allowed deletion");
            vm.SelectedJob = vm.Jobs.Single(j => j.Job.Active); await Execute(vm, vm.CancelCommand);
            Require(delete.IsEnabled, "Cancellation left definition locked");
            await Execute(vm, vm.ReleaseCommand);
            Require(!delete.IsEnabled && vm.ScenarioDeletionHint.Contains("사용"), "Deletion enabled without ownership");
            await Execute(vm, vm.AcquireCommand); await Click(vm, delete);
            Require(vm.Scenarios.Count == 0 && vm.DraftSteps.Count == 0 && vm.ScenarioName == "" && !delete.IsEnabled &&
                vm.Jobs.Any(j => j.Id == completed.Id && j.Job.Status == JobStatus.Completed), "Deletion left stale editor or lost history");
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.Scenarios.Count == 0 && vm.Jobs.Count == 2, "Reconnect restored deleted definition or lost history");

            // Single-step boundaries and 100-step endpoint selection.
            vm.DraftSteps.Add(a); await Select(0);
            Require(moves.All(button => !button.IsEnabled) && remove.IsEnabled, "Single-step movement enabled");
            await Click(vm, remove); Require(grid.SelectedIndex == -1 && !remove.IsEnabled, "Last deletion kept invalid selection");
            for (var i = 0; i < 100; i++) vm.DraftSteps.Add(b with { Value = i });
            await Select(99); var lastStep = vm.DraftSteps[99]; await Click(vm, first);
            Require(ReferenceEquals(vm.DraftSteps[0], lastStep) && grid.SelectedIndex == 0, "100-step first move failed");
            await Click(vm, last);
            Require(ReferenceEquals(vm.DraftSteps[99], lastStep) && grid.SelectedIndex == 99, "100-step last move failed");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Scenario editor binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "scenario-editor-result.txt"),
                "PASS: four real WPF move buttons, index selection with equal duplicate steps, precise deletion, boundaries and 100 steps, polling retention, local editing without lease, saved/load/executed order including wait and settings, selected definition deletion independent of draft, active/ownership guards, completed history and reconnect persistence, compact layout, no binding warnings. Isolated HTTPS host; no operational commands.");
            Console.WriteLine("Scenario editor WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
