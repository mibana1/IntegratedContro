using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Xml.Linq;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHandover()
    {
        await using var fixture = new HiperwallEditorFixture();
        // Two pending closes let the next operator cancel one while the first awaits a response.
        var instances = XElement.Parse(fixture.Server.Instances);
        var second = new XElement(instances.Elements().Single());
        second.Element("Instance")!.Element("id")!.Value = "external-2";
        instances.Add(second); fixture.Server.Instances = instances.ToString();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalHandler = fixture.Server.Handler!;
        fixture.Server.Handler = async request =>
        {
            if (request.Method == "POST" && XElement.Parse(request.Body).Element("command")?.Attribute("type")?.Value == "close")
            {
                started.TrySetResult();
                await release.Task;
                return new FakeHiperwallServer.Response("", Disconnect: true);
            }
            return await originalHandler(request);
        };
        await using var host = new HostProcess(); await host.Initialize();
        var (client, original) = await host.Login();
        var lease = await HostProcess.Post<Lease>(client, "/api/lease/acquire");
        await HostProcess.Post<IntegratedContro.Core.HiperwallSettingsView>(client, "/api/hiperwall/settings",
            new SaveHiperwallRequest(lease.Generation, 0, "교대 검증 Controller", fixture.Server.Endpoint,
                HiperwallAuthentication.Token, "3", 30000, FakeHiperwallServer.FixtureSecret));
        var inventory = await HostProcess.Post<IntegratedContro.Core.HiperwallView>(client, "/api/hiperwall/refresh");
        var request = new HiperwallEditRequest(Guid.NewGuid(), lease.Generation, 1, HiperwallEditAction.CloseAll,
            ExpectedRevision: HiperwallEditing.Revision(inventory.Instances.Items));
        await HostProcess.Post<HiperwallEditReceipt>(client, "/api/hiperwall/edit", request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await HostProcess.Post<Lease>(client, "/api/lease/release", new LeaseRequest(lease.Generation));

        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand);
            // No Hiperwall refresh/history action is needed to discover work.
            Require(vm.PreviousSummary.StartsWith("이전 사용자 작업 1건"), "Handover omitted Hiperwall at login");
            Require(vm.HiperwallJobs.Count == 1 && vm.HiperwallJobs[0].PreviousSession, "Same-account previous session missing");
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HandoverTab");
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("HiperwallJobGrid");
            grid.SelectedItem = vm.HiperwallJobs.Single();
            Require(vm.SelectedHiperwallJob?.Id == request.RequestId, "Hiperwall handover selection binding failed");
            Require(vm.HiperwallJobDetails.Contains(original.Session.Id.ToString()) &&
                vm.HiperwallJobDetails.Contains(fixture.Server.Endpoint), "Original session/target missing");
            Require(!vm.CancelHiperwallJobCommand.CanExecute(null), "Read-only session could cancel");
            await Execute(vm, vm.AcquireCommand);
            Require(vm.CancelHiperwallJobCommand.CanExecute(null), "New owner cannot cancel pending previous work");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            Capture(window, Path.Combine(output, "handover-hiperwall.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "handover-hiperwall-small.png"));
            await Click(vm, (Button)window.FindName("CancelHiperwallJob"));
            var receipt = (await HostProcess.State(client)).OutstandingHiperwallEdits.Single();
            Require(receipt.Steps.Count(s => s.State == HiperwallSendState.Rejected) == 1 &&
                receipt.Steps.Count(s => s.State == HiperwallSendState.Sending) == 1, "Cancel affected already sent command");
            release.TrySetResult();
            await HostProcess.Until(client, s => s.OutstandingHiperwallEdits.Single().Steps.Any(x => x.State == HiperwallSendState.Unknown));
            // The regular state poll must pick up the uncertainty without a manual refresh.
            await Wait(() => vm.HiperwallJobs.Single().Receipt!.Steps.Any(s => s.State == HiperwallSendState.Unknown));
            Require(vm.PreviousSummary.StartsWith("이전 사용자 작업 1건"), "Unknown disappeared from handover count");
            Require(!vm.CancelHiperwallJobCommand.CanExecute(null), "Unknown-only work advertised cancellable");
            await Execute(vm, vm.LogoutCommand);
            Require(vm.HiperwallJobs.Count == 0 && vm.SelectedHiperwallJob is null, "Logout retained handover entries");
            await Execute(vm, vm.LoginCommand);
            Require(vm.HiperwallJobs.Count == 1 && vm.PreviousSummary.StartsWith("이전 사용자 작업 1건"),
                "Relogin did not automatically restore outstanding handover");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Handover binding errors: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "handover-result.txt"),
                "PASS: real HTTPS host and WPF login/state polling; same-account earlier session; original requester/target; viewer cancellation disabled; owner cancels only pending close; unknown remains; logout clears and relogin reloads. Fake Controller only.");
        }
        finally
        {
            release.TrySetResult();
            window.Close(); await Wait(() => !window.IsVisible);
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
