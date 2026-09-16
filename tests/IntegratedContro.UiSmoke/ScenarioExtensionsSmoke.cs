using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunScenarioExtensions()
    {
        await using var fixture = new HiperwallEditorFixture();
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !h.IsBusy); Require(command.CanExecute(null), h.Message);
            command.Execute(null); await Wait(() => !h.IsBusy);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            vm.DeviceName = "조건 확인 조명"; vm.ConnectionId = "scenario-smoke";
            vm.SelectedModel = vm.Models.Single(m => m.Id == "virtual-light");
            await Execute(vm, vm.SaveDeviceCommand); vm.SelectedDevice = vm.Devices.Single(); vm.RoleName = "scenario.light";
            await Execute(vm, vm.SaveRoleCommand);
            await Hiper(h.LoadSettingsCommand); h.Name = "시나리오 Controller"; h.Endpoint = fixture.Server.Endpoint;
            h.User = "3"; h.Authentication = HiperwallAuthentication.Token; h.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand); await Execute(vm, vm.RefreshCommand); await Hiper(h.RefreshCommand);
            h.DraftContent = h.Contents[0]; h.DraftZone = h.Zones[0];
            h.DraftX = "-25.5"; h.DraftY = "0"; h.DraftWidth = "640"; h.DraftHeight = "360";
            await Hiper(h.AddPlacementCommand); h.LayoutName = "시나리오 배치"; h.DurationMode = DisplayDurationMode.Default;
            await Hiper(h.SaveLayoutCommand); await Execute(vm, vm.RefreshCommand);

            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("ScenarioTab"); window.UpdateLayout();
            var kind = (ComboBox)window.FindName("ScenarioKindPicker"); var add = (Button)window.FindName("AddScenarioStep");
            vm.ScenarioName = "조명 → 조건 확인 → 배치 표시 → 밝기";
            vm.SelectedRole = vm.Roles.Single(); vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Power);
            vm.CommandValue = 1; vm.DelayMs = 0; vm.TimeoutMs = 10000;
            await Click(vm, add);
            kind.SetCurrentValue(ComboBox.SelectedValueProperty, ScenarioStepKind.WaitUntil); window.UpdateLayout();
            Require(vm.DraftStepKind == ScenarioStepKind.WaitUntil && vm.IsDeviceScenarioStep, "Wait kind binding failed");
            await Click(vm, add);
            kind.SetCurrentValue(ComboBox.SelectedValueProperty, ScenarioStepKind.DisplayLayout); window.UpdateLayout();
            var layouts = (ComboBox)window.FindName("ScenarioLayoutPicker");
            layouts.SetCurrentValue(ComboBox.SelectedItemProperty, vm.ScenarioLayouts.Single()); window.UpdateLayout();
            Require(vm.SelectedScenarioLayout is not null && layouts.IsVisible, "Saved layout binding missing");
            await Click(vm, add);
            kind.SetCurrentValue(ComboBox.SelectedValueProperty, ScenarioStepKind.DeviceCommand);
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness); vm.CommandValue = 60;
            await Click(vm, add);
            Require(vm.DraftSteps.Select(s => s.Kind).SequenceEqual(new[] { ScenarioStepKind.DeviceCommand,
                ScenarioStepKind.WaitUntil, ScenarioStepKind.DisplayLayout, ScenarioStepKind.DeviceCommand }), "Mixed draft order lost");
            await Execute(vm, vm.SaveScenarioCommand); vm.SelectedScenario = vm.Scenarios.Single();
            await Execute(vm, vm.LoadScenarioCommand);
            Require(vm.DraftSteps.Count == 4 && vm.DraftSteps[2].LayoutId == vm.ScenarioLayouts.Single().Id, "Mixed definition round-trip failed");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            Capture(window, Path.Combine(output, "scenario-extensions.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "scenario-extensions-small.png"));
            await Execute(vm, vm.RunScenarioCommand); await Wait(() => vm.Jobs.Any(j => j.Job.Status == JobStatus.Completed));
            var completed = vm.Jobs.Single().Job;
            Require(completed.Steps.Select(s => s.Status).SequenceEqual(new[] { StepStatus.Simulated, StepStatus.ConditionMet,
                StepStatus.Acknowledged, StepStatus.Simulated }), "Mixed execution order/result incorrect");
            Require(fixture.Commands.Count == 1 && vm.HiperwallJobs.Any(), "Scenario display not visible in handover");
            vm.SelectedJob = vm.Jobs.Single(); Require(vm.JobDetails.Contains("시나리오 배치") && vm.JobDetails.Contains("조건 충족"), "Mixed details missing");
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HandoverTab"); window.UpdateLayout();
            Capture(window, Path.Combine(output, "scenario-results.png"));

            await Execute(vm, vm.NewScenarioCommand); vm.ScenarioName = "교대 중 조건 대기"; vm.DraftStepKind = ScenarioStepKind.WaitUntil;
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Power); vm.CommandValue = 0; vm.TimeoutMs = 60000;
            await Execute(vm, vm.AddStepCommand); vm.DraftStepKind = ScenarioStepKind.DisplayLayout;
            await Execute(vm, vm.AddStepCommand); await Execute(vm, vm.SaveScenarioCommand);
            vm.SelectedScenario = vm.Scenarios.Single(s => s.Name == "교대 중 조건 대기"); await Execute(vm, vm.RunScenarioCommand);
            await Wait(() => vm.Jobs.Any(j => j.Job.Steps[0].Status == StepStatus.Waiting));
            var waitingId = vm.Jobs.Single(j => j.Job.Active).Id;
            await Execute(vm, vm.ReleaseCommand); await Execute(vm, vm.LogoutCommand);
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            vm.SelectedJob = vm.Jobs.Single(j => j.Id == waitingId);
            Require(vm.SelectedJob.PreviousSession && vm.SelectedJob.Job.Active, "Accepted wait lost at handover");
            await Execute(vm, vm.CancelCommand);
            Require(vm.Jobs.Single(j => j.Id == waitingId).Job.Status == JobStatus.Cancelled && fixture.Commands.Count == 1,
                "Cancel allowed following display or failed to stop wait");
            await Wait(() => !vm.HiperwallJobs.Any(j => j.Display?.Outstanding == true), 25000);
            Require(fixture.Commands.Count == 2 && fixture.Server.Instances.Contains("external-1"), "Completed scenario display cleanup failed");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Scenario binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "scenario-extensions-result.txt"),
                "PASS: actual WPF step/layout selector bindings; mixed scenario edit/save/load; device command → read-only wait → frozen saved layout display → device command; protocol result and child display handover; lease release/logout preserves wait; next session cancellation blocks following display; completed scenario timed cleanup preserves external instance. Isolated HTTPS host and fake Controller. Full and compact rendering; no binding warnings.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
