using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
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
        var (client, _) = await host.Login();
        var lease = await HostProcess.Post<Lease>(client, "/api/lease/acquire");
        var device = await HostProcess.Post<DeviceConfig>(client, "/api/devices", new DeviceRequest(lease.Generation,
            Guid.NewGuid(), Guid.NewGuid(), "테스트 장비 PC", "시나리오 조명", "virtual", "virtual-light"));
        await HostProcess.Post<RoleBinding>(client, "/api/roles", new RoleRequest(lease.Generation, "scenario.light", device.Id));
        await HostProcess.Post<IntegratedContro.Core.HiperwallSettingsView>(client, "/api/hiperwall/settings", new SaveHiperwallRequest(lease.Generation,
            0, "시나리오 검증", fixture.Server.Endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        await HostProcess.Post<Lease>(client, "/api/lease/release", new LeaseRequest(lease.Generation));
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        async Task WaitForJob(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!ready() && DateTime.UtcNow < deadline) { await Execute(vm, vm.RefreshCommand); await Task.Delay(100); }
            Require(ready(), "Scenario job did not reach the expected state: " + vm.Message);
        }
        async Task RefreshHiperwall()
        {
            await Wait(() => !h.IsBusy); h.RefreshCommand.Execute(null); await Wait(() => !h.IsBusy);
            Require(h.Instances.Count > 0, "LIVE inventory missing: " + h.Message);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand); await RefreshHiperwall();
            var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedItem = window.FindName("SavedLayoutsTab");
            vm.SavedLayoutName = "행사 시작 화면";
            await Click(vm, (Button)window.FindName("CaptureLayout"));
            Require(vm.SavedLayouts.Count == 1 && fixture.Commands.IsEmpty, "Saving a layout sent LIVE commands");
            Require(vm.SelectedSavedLayout is not null && vm.SavedLayoutDetails.Contains("640×360"), "Saved layout details lost geometry");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "saved-layouts.png"));
            vm.SelectedRole = vm.Roles.Single(r => r.Id == "scenario.light");
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Power);
            vm.CommandValue = 1; vm.DelayMs = 0;
            tabs.SelectedItem = window.FindName("ScenarioTab");
            vm.ScenarioName = "조명 확인 후 화면 표시";
            await Execute(vm, vm.AddStepCommand);
            await Click(vm, (Button)window.FindName("AddWaitStep"));
            await Click(vm, (Button)window.FindName("AddLayoutStep"));
            Require(vm.DraftSteps.Count == 3 && vm.DraftSteps[1].Kind == ScenarioStepKind.WaitUntil &&
                vm.DraftSteps[2].Kind == ScenarioStepKind.ShowLayout, "Mixed scenario editor lost step kinds");
            await Execute(vm, vm.SaveScenarioCommand);
            vm.SelectedScenario = vm.Scenarios.Single(s => s.Name == vm.ScenarioName);
            window.UpdateLayout(); Capture(window, Path.Combine(output, "scenario-extensions.png"));
            await Execute(vm, vm.RunScenarioCommand);
            await WaitForJob(() => vm.Jobs.Any(j => j.Name == vm.ScenarioName && !j.Job.Active));
            var completed = vm.Jobs.Single(j => j.Name == vm.ScenarioName);
            Require(completed.Job.Status == JobStatus.Completed && completed.Job.Steps[1].SentAt is null &&
                completed.Job.Steps[1].ObservationsChecked >= 1, "Mixed scenario did not wait/read/display correctly: " + completed.Result);
            Require(fixture.Commands.Count == 1, "Scenario sent the wrong number of display commands");
            vm.SelectedJob = completed; Require(vm.JobDetails.Contains("행사 시작 화면") && vm.JobDetails.Contains("조건 조회"), "Mixed job details missing");
            tabs.SelectedItem = window.FindName("SavedLayoutsTab");
            await Click(vm, (Button)window.FindName("DisplayLayout"));
            await WaitForJob(() => vm.Jobs.Any(j => j.Job.Kind == JobKind.LayoutDisplay && !j.Job.Active));
            Require(fixture.Commands.Count == 2, "Manual saved layout display did not execute once");
            await Execute(vm, vm.NewScenarioCommand); vm.ScenarioName = "대기 중 교대 취소"; vm.CommandValue = 0;
            await Click(vm, (Button)window.FindName("AddWaitStep")); await Click(vm, (Button)window.FindName("AddLayoutStep"));
            await Execute(vm, vm.SaveScenarioCommand); vm.SelectedScenario = vm.Scenarios.Single(s => s.Name == vm.ScenarioName);
            await Execute(vm, vm.RunScenarioCommand);
            await WaitForJob(() => vm.Jobs.Any(j => j.Name == vm.ScenarioName && j.Job.Steps[0].Status == StepStatus.Waiting));
            Require(!h.CanOperate, "Hiperwall manual edit remained enabled during reservation");
            await Execute(vm, vm.ReleaseCommand); await Execute(vm, vm.AcquireCommand);
            vm.SelectedJob = vm.Jobs.Single(j => j.Name == vm.ScenarioName); await Execute(vm, vm.CancelCommand);
            Require(vm.Jobs.Single(j => j.Name == vm.ScenarioName).Job.Status == JobStatus.Cancelled && fixture.Commands.Count == 2,
                "Cancelling wait leaked display commands");
            await Execute(vm, vm.LogoutCommand); Require(vm.SavedLayouts.Count == 0, "Logout retained layouts");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Scenario bindings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "scenario-extensions-result.txt"),
                "PASS: read-only LIVE capture; saved layout details; device→wait→display scenario; manual display; reservation; release/acquire and pending cancellation; logout clearing; 1180x860 WPF. Isolated HTTPS host and fake Controller only.");
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible);
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
