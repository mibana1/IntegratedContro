using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunDraftRecovery()
    {
        await using var fixture = new HiperwallEditorFixture();
        await using var media = new FakeHiperwallServer();
        media.Handler = request => Task.FromResult(new FakeHiperwallServer.Response("{}",
            request.Method == "GET" ? 404 : 200) { ContentType = "application/json" });
        await using var host = new HostProcess(); await host.Initialize(300);
        var window = new MainWindow(false);
        var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall; var camera = vm.Cameras;
        // Inject failures at the shared reporting boundary, then recover through real HTTPS state reads.
        // Stop the timer only to keep an unrelated successful poll from racing each failure assertion.
        ((DispatcherTimer)typeof(MainViewModel).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!).Stop();
        var report = typeof(MainViewModel).GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !h.IsBusy); Require(command.CanExecute(null), "Hiperwall command unavailable: " + h.Message);
            command.Execute(null); await Wait(() => !h.IsBusy);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            await Hiper(h.LoadSettingsCommand); h.Name = "초안 보존 Controller"; h.Endpoint = fixture.Server.Endpoint;
            h.User = "3"; h.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand); await Execute(vm, vm.RefreshCommand); await Hiper(h.RefreshCommand);
            h.DraftContent = h.Contents[0]; h.DraftZone = h.Zones[0]; h.DraftX = "-50.5"; h.DraftY = "100.25";
            h.DraftWidth = "640"; h.DraftHeight = "360";
            await Hiper(h.AddPlacementCommand); h.LayoutName = "저장된 배치"; await Hiper(h.SaveLayoutCommand);
            var layoutId = h.SelectedLayout!.Id;
            h.LayoutName = "미저장 배치 수정"; h.SelectedPlacement = h.DraftPlacements[0];
            h.DraftX = "-123."; h.DraftY = "25.75"; h.DraftWidth = "712.5"; h.DraftHeight = "400.25";
            h.DurationMode = DisplayDurationMode.Timed; h.DurationSeconds = "47";
            var placement = h.SelectedPlacement; var content = h.DraftContent; var zone = h.DraftZone;
            var layoutVersion = h.DraftVersionLabel;

            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("CameraTab");
            await Wait(() => !camera.IsBusy && camera.CanConfigure);
            camera.ApiEndpoint = media.Endpoint; camera.HlsEndpoint = media.Endpoint;
            camera.ApiUser = "api"; camera.HlsUser = "reader";
            camera.ReadApiPassword = () => "fixture-api"; camera.ReadHlsPassword = () => "fixture-hls";
            await CameraExecute(camera, camera.SaveSettingsCommand);
            var view = (IntegratedContro.App.CameraView)window.FindName("CameraWorkspace");
            var rtsp = (PasswordBox)view.FindName("RtspInput");
            var user = (PasswordBox)view.FindName("RtspUserInput");
            var password = (PasswordBox)view.FindName("RtspPasswordInput");
            camera.CameraName = "저장된 카메라"; camera.Enabled = false; rtsp.Password = "rtsp://127.0.0.1/fixture";
            await CameraExecute(camera, camera.SaveCommand);
            var cameraId = camera.Cameras.Single().Id;
            camera.Selected = camera.Cameras.Single(); await CameraExecute(camera, camera.LoadCommand);
            camera.CameraName = "미저장 카메라 수정"; camera.Location = "입력 중 위치"; camera.SelectedMapping = h.Contents[0];
            camera.ApiEndpoint = "http://127.0.0.1:12345"; camera.ApiUser = "미저장 API 계정";
            rtsp.Password = "rtsp://127.0.0.1/draft"; user.Password = "fixture-user"; password.Password = "fixture-password";
            var cameraVersion = camera.DraftSummary; var mapping = camera.SelectedMapping;
            void RequireDrafts()
            {
                Require(h.LayoutName == "미저장 배치 수정" && h.DraftPlacements.Count == 1 &&
                    ReferenceEquals(h.SelectedPlacement, placement) && h.DraftVersionLabel == layoutVersion &&
                    h.DraftX == "-123." && h.DraftY == "25.75" && h.DraftWidth == "712.5" && h.DraftHeight == "400.25" &&
                    ReferenceEquals(h.DraftContent, content) && ReferenceEquals(h.DraftZone, zone) &&
                    h.DurationMode == DisplayDurationMode.Timed && h.DurationSeconds == "47",
                    "Connection failure erased the layout draft, editor selection, geometry input or stored version");
                Require(camera.CameraName == "미저장 카메라 수정" && camera.Location == "입력 중 위치" && !camera.Enabled &&
                    camera.DraftSummary == cameraVersion && camera.Selected?.Id == cameraId &&
                    ReferenceEquals(camera.SelectedMapping, mapping) && !camera.ClearMapping &&
                    camera.ApiEndpoint == "http://127.0.0.1:12345" && camera.ApiUser == "미저장 API 계정" &&
                    rtsp.Password == "rtsp://127.0.0.1/draft" && user.Password == "fixture-user" && password.Password == "fixture-password",
                    "Connection failure erased the camera draft, mapping, settings input or protected inputs");
            }
            foreach (var error in new Exception[]
            {
                new HttpRequestException("fixture transport failure"), new TaskCanceledException("fixture deadline"),
                new ApiException("unavailable", "fixture unavailable", HttpStatusCode.ServiceUnavailable)
            })
            {
                report.Invoke(vm, [error]);
                Require(vm.IsLoggedIn && !vm.CanControl, "Transient failure became logout or allowed control");
                RequireDrafts();
                Require(!h.SaveLayoutCommand.CanExecute(null) && !h.TestLayoutCommand.CanExecute(null) &&
                    !h.CanOperate && !h.CanPreview &&
                    !camera.SaveCommand.CanExecute(null) && !camera.PlayCommand.CanExecute(null) && !camera.RefreshCommand.CanExecute(null),
                    "Disconnected context allowed a network operation");
                // Repeat notifications while disconnected: preserving the draft must be idempotent.
                report.Invoke(vm, [error]); RequireDrafts();
                await Execute(vm, vm.RefreshCommand); await Wait(() => !h.IsBusy && !camera.IsBusy);
                RequireDrafts();
                Require(vm.CanControl && h.SaveLayoutCommand.CanExecute(null) && camera.SaveCommand.CanExecute(null),
                    "Successful state refresh did not restore permitted operations");
            }
            Require(fixture.Commands.IsEmpty && h.SavedLayouts.Single().Version == 1 && camera.Cameras.Single().Version == 1,
                "Recovery replayed a write or saved a local draft");
            await Hiper(h.SaveLayoutCommand); await CameraExecute(camera, camera.SaveCommand);
            Require(h.SavedLayouts.Single().Id == layoutId && h.SavedLayouts.Single().Version == 2 &&
                camera.Cameras.Single().Id == cameraId && camera.Cameras.Single().Version == 2,
                "Recovery lost draft IDs/versions and created duplicate records");

            await Hiper(h.NewLayoutCommand); h.LayoutName = "새 배치 입력"; h.DraftPlacements.Add(placement!);
            await CameraExecute(camera, camera.NewCommand); camera.CameraName = "새 카메라 입력";
            rtsp.Password = "rtsp://127.0.0.1/new-draft";
            report.Invoke(vm, [new HttpRequestException("fixture transport failure")]);
            Require(h.LayoutName == "새 배치 입력" && h.DraftPlacements.Count == 1 && camera.CameraName == "새 카메라 입력" &&
                rtsp.Password == "rtsp://127.0.0.1/new-draft", "New unsaved drafts did not survive connection loss");
            await Execute(vm, vm.LogoutCommand);
            Require(!vm.IsLoggedIn && h.DraftPlacements.Count == 0 && h.LayoutName == "" && camera.CameraName == "" &&
                camera.SelectedMapping is null && rtsp.Password == "" && user.Password == "" && password.Password == "",
                "Explicit logout retained the previous session's draft or secrets");
            await Execute(vm, vm.LoginCommand); await Wait(() => !h.IsBusy && !camera.IsBusy);
            Require(h.DraftPlacements.Count == 0 && camera.CameraName == "", "New session recovered a previous user's draft");
            h.LayoutName = "만료될 초안"; camera.CameraName = "만료될 카메라"; password.Password = "fixture-expired";
            report.Invoke(vm, [new ApiException("unauthorized", "fixture expired", HttpStatusCode.Unauthorized)]);
            Require(!vm.IsLoggedIn && h.LayoutName == "" && camera.CameraName == "" && password.Password == "",
                "Authentication expiry was treated as a transient connection failure");
            // An in-flight save is cancelled on connection loss without its late finally clearing the draft.
            using var stalled = new TcpListener(IPAddress.Loopback, 0); stalled.Start();
            using var stalledClient = new HostClient($"https://127.0.0.1:{((IPEndPoint)stalled.LocalEndpoint).Port}", new string('0', 64));
            var session = Guid.NewGuid();
            camera.UpdateContext(stalledClient, session, true, false, 0);
            camera.CameraName = "전송 중 보존"; rtsp.Password = "rtsp://127.0.0.1/pending"; password.Password = "fixture-pending";
            camera.SaveCommand.Execute(null); await Wait(() => camera.IsBusy);
            using var accepted = await stalled.AcceptTcpClientAsync();
            camera.UpdateContext(stalledClient, session, false, false, 0, connected: false);
            await Task.Delay(100);
            Require(!camera.IsBusy && camera.CameraName == "전송 중 보존" && rtsp.Password == "rtsp://127.0.0.1/pending" &&
                password.Password == "fixture-pending", "Cancelled save cleared protected draft inputs");
            camera.UpdateContext(stalledClient, Guid.NewGuid(), true, false, 0);
            Require(camera.CameraName == "" && rtsp.Password == "" && password.Password == "",
                "A different session inherited the disconnected draft");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "draft-recovery-result.txt"),
                "PASS: new/existing layout and camera drafts, geometry text, IDs/versions, selection/mapping and protected inputs survive transport/deadline/503 failures and repeated notifications; offline network operations blocked; real HTTPS refresh recovers without writes; save updates same records; logout/new session/401 clear drafts and secrets; in-flight save cancellation preserves protected inputs. Exception injection at MainViewModel boundary with real WPF/isolated HTTPS host.");
        }
        finally { await vm.CloseAsync(); window.Close(); }
    }
}