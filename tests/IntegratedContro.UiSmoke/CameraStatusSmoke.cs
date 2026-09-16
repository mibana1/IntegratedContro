using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunCameraStatus()
    {
        await using var host = new HostProcess(); await host.Initialize();
        await using var media = new FakeHiperwallServer();
        media.Handler = _ => Task.FromResult(new FakeHiperwallServer.Response("fixture rejection", 401));
        var window = new MainWindow(false);
        var vm = (MainViewModel)window.DataContext; var camera = vm.Cameras;
        const string secret = "fixture-camera-password";
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("CameraTab");
            var view = (IntegratedContro.App.CameraView)window.FindName("CameraWorkspace");
            await Wait(() => view.IsLoaded && view.IsVisible && camera.CanConfigure && !camera.IsBusy);
            var footer = FindAll<TextBlock>(window).Single(t => AutomationProperties.GetAutomationId(t) == "StatusMessage");
            async Task Expect(string reason)
            {
                try { await Wait(() => footer.Text.Contains(reason), 1500); }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException($"Expected status: {reason}; camera: {camera.Message}; main: {vm.Message}; footer: {footer.Text}".Replace(secret, "<redacted>"));
                }
                Require(vm.Message == footer.Text && camera.Message == footer.Text, "Camera failure did not reach the shared footer");
                Require(!footer.Text.Contains(secret), "Camera status exposed a password");
            }

            camera.CameraName = "오류 확인 카메라"; camera.ReadRtsp = () => "rtsp://127.0.0.1/example";
            await CameraExecute(camera, camera.SaveCommand);
            await Expect("MediaMTX 설정을 먼저 저장하세요.");

            camera.ApiEndpoint = media.Endpoint; camera.HlsEndpoint = media.Endpoint;
            camera.ApiUser = "api"; camera.HlsUser = "reader";
            camera.ReadApiPassword = () => secret; camera.ReadHlsPassword = () => secret;
            await CameraExecute(camera, camera.SaveSettingsCommand);
            camera.ReadRtsp = () => "https://invalid-camera/" + secret;
            await CameraExecute(camera, camera.SaveCommand);
            await Expect("유효한 rtsp:// 또는 rtsps:// 주소를 입력하세요.");
            camera.ReadRtsp = () => "";
            await CameraExecute(camera, camera.SaveCommand);
            await Expect("RTSP 주소도 입력하세요.");

            camera.ReadRtsp = () => "rtsp://127.0.0.1/example";
            camera.ReadRtspUser = () => "camera"; camera.ReadRtspPassword = () => secret;
            await CameraExecute(camera, camera.SaveCommand);
            await Wait(() => camera.Cameras.SingleOrDefault()?.Provisioning == CameraProvisioning.Failed, 15000);
            await Expect("MediaMTX 전용 계정의 인증·권한을 확인하세요.");
            Require(footer.Text.Contains("오류 확인 카메라") && footer.Text.Contains("경로 준비 실패"),
                "Asynchronous failure omitted the camera name or stage");

            await Execute(vm, vm.ReleaseCommand);
            var released = vm.Message;
            await Task.Delay(2500);
            Require(footer.Text == released, "Unchanged periodic failure overwrote a newer status");
            camera.Selected = camera.Cameras.Single();
            await Expect("MediaMTX 전용 계정의 인증·권한을 확인하세요.");

            await Execute(vm, vm.AcquireCommand);
            media.Handler = request => Task.FromResult(new FakeHiperwallServer.Response("{}",
                request.Method == "GET" ? 404 : 200) { ContentType = "application/json" });
            await CameraExecute(camera, camera.NewCommand);
            camera.CameraName = "성공 확인 카메라";
            var savedMessages = new List<string>();
            PropertyChangedEventHandler onStatus = (_, e) => { if (e.PropertyName == nameof(vm.Message)) savedMessages.Add(vm.Message); };
            vm.PropertyChanged += onStatus;
            try { await CameraExecute(camera, camera.SaveCommand); }
            finally { vm.PropertyChanged -= onStatus; }
            Require(savedMessages.Any(m => m.Contains("카메라 등록 성공") && m.Contains("성공 확인 카메라") && m.Contains("영상 경로 준비 중")),
                "Successful registration did not immediately publish its name and pending state");
            await Wait(() => camera.Cameras.Any(c => c.Name == "성공 확인 카메라" && c.Provisioning == CameraProvisioning.Ready), 15000);
            await Expect("카메라 등록 성공");
            await Expect("영상 경로 준비 완료");

            camera.Selected = camera.Cameras.Single(c => c.Name == "성공 확인 카메라");
            await CameraExecute(camera, camera.LoadCommand);
            camera.CameraName = "수정 확인 카메라";
            await CameraExecute(camera, camera.SaveCommand);
            await Wait(() => camera.Cameras.Any(c => c.Name == "수정 확인 카메라" && c.Provisioning == CameraProvisioning.Ready), 15000);
            await Expect("카메라 수정 성공");
            Require(footer.Text.Contains("수정 확인 카메라"), "Update success omitted the camera name");
            camera.Enabled = false;
            await CameraExecute(camera, camera.SaveCommand);
            await Wait(() => camera.Cameras.Any(c => c.Name == "수정 확인 카메라" && c.Provisioning == CameraProvisioning.Disabled), 15000);
            await Expect("카메라 수정 성공");
            await Expect("비활성 상태로 저장되었습니다.");

            await Execute(vm, vm.LogoutCommand);
            // A private TCP listener leaves TLS pending to exercise the real HTTP request deadline.
            using var stalled = new TcpListener(IPAddress.Loopback, 0); stalled.Start();
            using var stalledClient = new HostClient($"https://127.0.0.1:{((IPEndPoint)stalled.LocalEndpoint).Port}", new string('0', 64));
            camera.UpdateContext(stalledClient, Guid.NewGuid(), true, false, 0);
            camera.CameraName = "응답 지연 카메라";
            await CameraExecute(camera, camera.SaveCommand);
            await Expect("응답 시간");
            Require(footer.Text.Contains("결과"), "Timeout claimed a definite save failure");

            var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "camera-status-result.txt"),
                "PASS: real WPF footer binding; registration success immediately and after provisioning; update success; disabled-camera success; missing MediaMTX settings; invalid/missing RTSP; asynchronous MediaMTX 401 with camera name/stage; unchanged polls preserve newer messages; selected failed camera; real HTTP timeout; no credential disclosure.\n");
            Console.WriteLine("PASS: camera registration/update success and provisioning failures reach the footer with their details.");
        }
        finally { await vm.CloseAsync(); window.Close(); await Task.Delay(100); }
    }
}