using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwall()
    {
        await using var server = new FakeHiperwallServer();
        await using var host = new HostProcess(); await host.Initialize();
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
        var window = new MainWindow(false);
        var vm = (MainViewModel)window.DataContext;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !vm.Hiperwall.IsBusy);
            Require(command.CanExecute(null), "Hiperwall command unavailable: " + vm.Hiperwall.Message);
            command.Execute(null);
            await Wait(() => !vm.Hiperwall.IsBusy);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Wait(() => !vm.Hiperwall.IsBusy);
            await Hiper(vm.Hiperwall.RefreshCommand);
            Require(vm.Hiperwall.Status == "설정되지 않음", vm.Hiperwall.Message);
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedItem = window.FindName("HiperwallTab"); window.UpdateLayout();
            Capture(window, Path.Combine(output, "hiperwall-unconfigured.png"));
            Require(!vm.Hiperwall.SaveCommand.CanExecute(null), "Settings save allowed without lease");
            await Execute(vm, vm.AcquireCommand);
            tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();
            await Hiper(vm.Hiperwall.LoadSettingsCommand);
            var h = vm.Hiperwall;
            IHiperwallContentLookup lookup = h;
            var cameraChoices = vm.Cameras.MappingContents;
            h.Name = "가짜 Controller 검증"; h.Endpoint = server.Endpoint; h.User = "3";
            h.TimeoutText = "3000"; h.Authentication = HiperwallAuthentication.Token;
            var settings = (IntegratedContro.App.HiperwallSettingsView)window.FindName("HiperwallSettings");
            var token = (PasswordBox)settings.FindName("TokenInput");
            token.Password = FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand);
            Require(token.Password == "" && h.AppliedSettings.Contains("v1"), "Save did not clear token/apply settings: " + h.Message);
            await Execute(vm, vm.RefreshCommand);
            window.UpdateLayout(); Capture(window, Path.Combine(output, "hiperwall-settings.png"));
            await Hiper(h.TestCommand);
            Require(h.Contents.Count == 3 && h.Zones.Count == 2 && h.Walls.Count == 0, h.Message);
            Require(ReferenceEquals(cameraChoices, lookup.Contents) && cameraChoices.SequenceEqual(h.Contents) && lookup.ConfigurationVersion == 1,
                "Camera lookup did not receive the current wall inventory/version");
            Require(h.Status == "연결 성공" && h.WallState.Contains("미지원"), "Inventory status was conflated");
            await Hiper(h.LoadSettingsCommand);
            Require(token.Password == "" && h.SecretStatus.Contains("저장됨"), "Saved token was returned to editor");
            // Names are deliberately identical; selecting the second row must keep its own UUID.
            tabs.SelectedItem = window.FindName("HiperwallTab"); window.UpdateLayout();
            var inventory = (IntegratedContro.App.HiperwallView)window.FindName("HiperwallInventory");
            window.UpdateLayout();
            var contents = (ListBox)inventory.FindName("ContentList");
            contents.SetCurrentValue(ListBox.SelectedItemProperty, h.Contents[1]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(h.Details.Contains("uuid-2"), "Duplicate names redirected selection");
            Capture(window, Path.Combine(output, "hiperwall-contents.png"));
            Find<TabControl>(inventory)!.SelectedIndex = 1; window.UpdateLayout();
            ((DataGrid)inventory.FindName("ZoneGrid")).SetCurrentValue(DataGrid.SelectedItemProperty, h.Zones[0]);
            Capture(window, Path.Combine(output, "hiperwall-zones.png"));
            var canvas = (HiperwallCanvas)inventory.FindName("WallCanvas");
            Require(h.Instances.Count == 2 && h.CanvasItems.Count == 4, "Missing fixture instances/geometry");
            ((TextBox)inventory.FindName("ContentSearch")).SetCurrentValue(TextBox.TextProperty, "uuid-2");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(h.FilteredContents.Cast<object>().Count() == 1 && h.Search == "uuid-2", "Search binding did not filter by UUID");
            Require(cameraChoices.Count == 3, "Wall filtering changed the camera mapping catalog");
            h.Search = "없는 검색어"; Require(h.FilteredContents.IsEmpty && h.ContentCount.Contains("검색 0"), "No-match search was not distinct from inventory");
            h.Search = ""; h.TypeFilter = "image";
            Require(h.FilteredContents.Cast<object>().Count() == 3, "Type filter lost response items");
            canvas.SetCurrentValue(HiperwallCanvas.SelectedProperty, h.Instances[1]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(h.Details.Contains("instance-2") && h.Details.Contains("content.uuid: uuid-1"), "Canvas selection confused content UUID and instance ID");
            var center = canvas.ToScreen(960, 540);
            Require(ReferenceEquals(canvas.ItemAt(center), h.Instances[1]), "Canvas geometry hit test chose a different instance");
            var scale = canvas.ViewScale;
            canvas.Zoom(1.2); Require(canvas.ViewScale > scale, "Canvas zoom did not change view");
            canvas.FitSelected(); Require(ReferenceEquals(canvas.ItemAt(canvas.ToScreen(960, 540)), h.Instances[1]), "Selected fit changed instance identity");
            canvas.FitAll(); window.UpdateLayout();
            Capture(window, Path.Combine(output, "hiperwall-workspace.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "hiperwall-workspace-small.png"));
            Require(canvas.ActualWidth >= 300 && canvas.ActualHeight >= 70, $"Workspace unusable at supported minimum size: {canvas.ActualWidth}x{canvas.ActualHeight}");
            window.Width = 1500; window.Height = 1000; window.UpdateLayout();
            var originalContents = server.Contents;
            server.Contents = "<Objects><Object type='environment'><name>environments\\이름.hwe</name><uuid>u3</uuid></Object></Objects>";
            await Hiper(h.RefreshCommand);
            Require(h.Contents[0].Folder == "environments", "Response path grouping lost");
            h.TypeFilter = "environment"; Require(h.FilteredContents.Cast<object>().Count() == 1, "Updated type filter failed");
            server.Contents = originalContents;
            var originalInstances = server.Instances;
            server.Instances = "<Objects><Object><name>필터 미지원</name></Object></Objects>";
            await Hiper(h.RefreshCommand);
            Require(h.Instances.Count == 0 && h.InstanceState.Contains("미지원") && h.CanvasItems.Count == 2, "Unsupported instance list retained stale rectangles");
            server.Instances = originalInstances;


            server.Contents = "<Objects/>";
            await Hiper(h.RefreshCommand);
            Require(h.ContentState.Contains("빈 목록"), "Empty response shown as failure");
            server.Contents = "<broken/>";
            await Hiper(h.RefreshCommand);
            Require(h.Contents.Count == 0 && h.ContentState.Contains("미지원"), "Invalid response retained stale content");
            Require(ReferenceEquals(cameraChoices, vm.Cameras.MappingContents) && cameraChoices.Count == 0, "Invalid contents left stale camera choices");
            Capture(window, Path.Combine(output, "hiperwall-unsupported.png"));

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Handler = request =>
            {
                var response = server.Default(request);
                if (request.Body.Contains("list")) { started.TrySetResult(); response = response with { DelayMs = 1400 }; }
                return Task.FromResult(response);
            };
            await Execute(vm, vm.ReleaseCommand);
            var (admin, _) = await host.Login(); var lease = await HostProcess.Post<Lease>(admin, "/api/lease/acquire");
            h.RefreshCommand.Execute(null); await started.Task;
            Require(h.IsBusy && !h.RefreshCommand.CanExecute(null), "Duplicate refresh allowed");
            h.RefreshCommand.Execute(null);
            var before = server.Operations.Count;
            await HostProcess.Post<IntegratedContro.Core.HiperwallSettingsView>(admin, "/api/hiperwall/settings",
                new SaveHiperwallRequest(lease.Generation, 1, "변경된 가짜 Controller", server.Endpoint, HiperwallAuthentication.Token, "3", 3000));
            await Execute(vm, vm.RefreshCommand);
            await Wait(() => !h.IsBusy && h.CurrentConnection.Contains("v2"));
            await Task.Delay(1500);
            Require(h.Contents.Count == 0 && h.CanvasItems.Count == 0 && h.Instances.Count == 0 && h.CurrentConnection.Contains("변경된"), "Late previous-settings result was displayed");
            Require(cameraChoices.Count == 0 && lookup.ConfigurationVersion == 2, "Settings change did not invalidate the camera lookup/version");
            Require(server.Operations.Count <= before + 1, "Duplicate refresh reached the fixture");
            await HostProcess.Post<Lease>(admin, "/api/lease/release", new LeaseRequest(lease.Generation));

            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.RefreshCommand.Execute(null); await started.Task;
            await Execute(vm, vm.LogoutCommand);
            Require(h.Contents.Count == 0 && h.CanvasItems.Count == 0 && h.Instances.Count == 0 && !h.RefreshCommand.CanExecute(null), "Logout retained inventory/read access");
            Require(cameraChoices.Count == 0 && lookup.ConfigurationVersion == 0, "Logout retained camera lookup state");
            vm.LoginName = "admin"; await Execute(vm, vm.LoginCommand); await Wait(() => !h.IsBusy);
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.RefreshCommand.Execute(null); await started.Task;
            window.Close(); await Wait(() => !window.IsVisible);
            Require(host.Process is { HasExited: false } && h.Contents.Count == 0 && h.CanvasItems.Count == 0, "Window close stopped host or retained query");
            Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Hiperwall binding errors: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "hiperwall-result.txt"),
                "PASS: real WPF windows + separate HTTPS ControlHost + loopback fake Controller. Unconfigured/settings/token clearing, read test, Korean duplicate identity, search/type/folder filters, open instance IDs, geometry selection and zoom/fit, minimum window size, empty/invalid lists, duplicate refresh, settings change/late response, logout and close. No real Hiperwall success or wall display changes.");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            if (window.IsVisible) { window.Close(); await Wait(() => !window.IsVisible); }
        }
    }
}
