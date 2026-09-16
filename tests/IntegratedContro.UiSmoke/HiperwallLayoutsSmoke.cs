using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwallLayouts()
    {
        await using var fixture = new HiperwallEditorFixture();
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !h.IsBusy); Require(command.CanExecute(null), "Layout command unavailable: " + h.LayoutMessage + " / " + h.Message);
            command.Execute(null); await Wait(() => !h.IsBusy);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand); await Wait(() => !h.IsBusy);
            await Hiper(h.LoadSettingsCommand); h.Name = "배치 검증 Controller"; h.Endpoint = fixture.Server.Endpoint;
            h.User = "3"; h.Authentication = HiperwallAuthentication.Token; h.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand); await Execute(vm, vm.RefreshCommand); await Hiper(h.RefreshCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HiperwallTab");
            var view = (IntegratedContro.App.HiperwallView)window.FindName("HiperwallInventory");
            ((TabControl)view.FindName("HiperwallWorkTabs")).SelectedItem = view.FindName("LayoutsTab");
            window.UpdateLayout();
            await RunHiperwallLayoutCapture(vm, fixture, Hiper);
            h.DraftContent = h.Contents[0]; h.DraftZone = h.Zones[0]; h.DraftX = "-25.5"; h.DraftY = "0"; h.DraftWidth = "640"; h.DraftHeight = "360";
            await Hiper(h.AddPlacementCommand); h.LayoutName = "운영 배치"; h.DurationMode = DisplayDurationMode.Continuous;
            await Hiper(h.SaveLayoutCommand);
            Require(h.SavedLayouts.Count == 1 && fixture.Commands.Count == 0, "Saving changed LIVE or failed: " + h.LayoutMessage);
            await Hiper(h.ShowLayoutCommand);
            await Wait(() => h.DisplayJobs.Any(j => j.Targets.Any(t => t.OpenState == HiperwallSendState.Acknowledged)));
            Require(fixture.Commands.Count == 1, "Saved display did not open exactly once");
            h.DraftX = "999"; // Editing input does not alter an already accepted snapshot.
            await Execute(vm, vm.ReleaseCommand);
            Require(!h.ShowLayoutCommand.CanExecute(null) && !h.StopDisplayCommand.CanExecute(null), "Lease release left display mutations enabled");
            Require(fixture.Commands.Count == 1, "Lease release closed the display");
            await Execute(vm, vm.LogoutCommand);
            Require(h.SavedLayouts.Count == 0 && h.DisplayJobs.Count == 0, "Logout kept layout session data");
            await Execute(vm, vm.LoginCommand); await Wait(() => !h.IsBusy);
            await Hiper(h.RefreshLayoutsCommand);
            Require(h.SavedLayouts.Count == 1 && h.DisplayJobs.Count == 1, "Login failed to restore layouts and display history");
            Require(vm.PreviousSummary.StartsWith("이전 사용자 작업 1건"), "Previous display missing from handover");
            await Execute(vm, vm.AcquireCommand); await Hiper(h.RefreshCommand);
            h.SelectedLayout = h.SavedLayouts[0]; await Hiper(h.LoadLayoutCommand);
            Require(h.DraftPlacements[0].Layout.X == -25.5 && h.DurationMode == DisplayDurationMode.Continuous, "Stored draft did not round-trip");
            h.SelectedDisplay = h.DisplayJobs[0];
            await Hiper(h.StopDisplayCommand);
            await Wait(() => h.DisplayJobs.All(j => !j.Outstanding));
            Require(fixture.Commands.Count == 2 && fixture.Server.Instances.Contains("external-1"), "Stop failed or closed external instance");
            await Hiper(h.TestLayoutCommand);
            await Wait(() => h.DisplayJobs.Any(j => j.Name.Contains("15초") && j.Targets.Any(t => t.OpenState == HiperwallSendState.Acknowledged)));
            h.SelectedDisplay = h.DisplayJobs.First(j => j.Name.Contains("15초"));
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HiperwallTab");
            ((TabControl)view.FindName("HiperwallWorkTabs")).SelectedItem = view.FindName("LayoutsTab");
            window.UpdateLayout();
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            Capture(window, Path.Combine(output, "hiperwall-layouts.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "hiperwall-layouts-small.png"));
            await Wait(() => h.DisplayJobs.All(j => !j.Outstanding), 25000);
            Require(h.SavedLayouts.Count == 1 && fixture.Commands.Count == 4, "15-second draft test changed saved layouts or did not clean up");
            Require(!bindingLog.ToString().Contains("System.Windows.Data Error"), "Layout binding error: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "hiperwall-layouts-result.txt"),
                "PASS: WPF saved multi-content draft controls; save without LIVE; display; lease release/logout preservation; same-account earlier-session handover; reload; selected stop; unsaved 15-second test and automatic cleanup; external instance preservation. Isolated HTTPS host and fake Controller.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
