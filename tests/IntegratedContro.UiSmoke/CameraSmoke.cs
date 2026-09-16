using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task Wait(Func<bool> ready, int timeoutMs,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(ready))] string? expression = null)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(expression);
            await Task.Delay(50);
        }
    }
    private static async Task CameraExecute(CameraViewModel vm, AsyncCommand command)
    {
        await Wait(() => command.CanExecute(null), 15000);
        command.Execute(null);
        await Wait(() => !vm.IsBusy, 25000);
    }
    private static async Task RunRelay()
    {
        var fetched = 0; var blocked = false;
        var relay = new LoopbackVideoRelay(async (asset, ct) =>
        {
            Interlocked.Increment(ref fetched);
            if (blocked) await Task.Delay(Timeout.Infinite, ct);
            return new MediaPayload("#EXTM3U\n#EXTINF:2,\ns1.ts\n"u8.ToArray(), "application/vnd.apple.mpegurl");
        });
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var unauthorized = await http.GetAsync(relay.Source.Uri);
            Require(unauthorized.StatusCode == System.Net.HttpStatusCode.Unauthorized && fetched == 0, "Relay unauthenticated fetch");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(relay.Source.UserName + ":" + relay.Source.Password)));
            using var invalid = await http.GetAsync(new Uri(relay.Source.Uri, "forbidden.txt"));
            Require(invalid.StatusCode == System.Net.HttpStatusCode.BadRequest && fetched == 0, "Relay accepted arbitrary asset");
            using var manifest = await http.GetAsync(relay.Source.Uri);
            Require(manifest.IsSuccessStatusCode && fetched == 1, "Authenticated relay did not fetch");
            blocked = true;
            var reads = Enumerable.Range(0, 4).Select(_ => http.GetAsync(relay.Source.Uri)).ToArray();
            await Wait(() => fetched == 5 && relay.ActiveRequests == 4, 4000);
            await relay.DisposeAsync();
            foreach (var read in reads)
            {
                try { using var response = await read; } catch (System.Net.Http.HttpRequestException) { }
            }
            Require(relay.ActiveRequests == 0, "Relay retained requests after disposal");
        }
        finally { await relay.DisposeAsync(); }
        Console.WriteLine("PASS: loopback authentication, asset restrictions and cancellation of four pending reads.");
    }
    private static async Task RunMedia()
    {
        await using var host = new HostProcess(); await host.Initialize(60);
        await using var media = new NativeMediaFixture(host.Root); await media.StartAsync();
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        var window = new MainWindow(false) { Width = 1180, Height = 860 };
        var vm = (MainViewModel)window.DataContext; var camera = vm.Cameras;

        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint; vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("CameraTab");
            await Wait(() => camera.CanConfigure);
            camera.ApiEndpoint = media.ApiEndpoint; camera.HlsEndpoint = media.HlsEndpoint; camera.ApiUser = "api"; camera.HlsUser = "reader";
            camera.ReadApiPassword = () => NativeMediaFixture.ApiPassword; camera.ReadHlsPassword = () => NativeMediaFixture.HlsPassword;
            await CameraExecute(camera, camera.SaveSettingsCommand);
            Require(!camera.AppliedMedia.Contains("설정 전"), camera.Message + "\n" + media.Diagnostics);
            camera.CameraName = "로컬 테스트 카메라"; camera.Location = "생성 영상 · 640 × 360 · H.264 / AAC";
            camera.ReadRtsp = () => media.Source; camera.ReadRtspUser = () => "camera"; camera.ReadRtspPassword = () => NativeMediaFixture.RtspPassword;
            await CameraExecute(camera, camera.SaveCommand);
            await Wait(() => camera.Cameras.SingleOrDefault()?.Provisioning == CameraProvisioning.Ready, 20000);
            camera.Selected = camera.Cameras.Single();
            await CameraExecute(camera, camera.StatusCommand);
            Console.WriteLine("Camera input: " + camera.Message);
            using (var verify = new HostClient(host.Endpoint, host.Fingerprint))
            {
                var login = await verify.Post<LoginResult>("/api/login", new LoginRequest("admin", host.Password, Guid.NewGuid(), "media verification"));
                verify.SetToken(login.Token);
                var manifest = await verify.GetMedia(camera.Selected.Id, camera.Selected.Version, "index.m3u8", default);
                Require(System.Text.Encoding.UTF8.GetString(manifest.Bytes).StartsWith("#EXTM3U"), "Missing HLS manifest");
                var mainManifest = await verify.GetMedia(camera.Selected.Id, camera.Selected.Version, "main_stream.m3u8", default);
                Require(System.Text.Encoding.UTF8.GetString(mainManifest.Bytes).Contains("#EXTINF"), "Missing HLS segments");
                await verify.Post<bool>("/api/logout");
            }
            await CameraExecute(camera, camera.PlayCommand);
            await Wait(() => camera.PlaybackMessage.Contains("실시간 영상 재생 중"), 40000);
            Require(camera.DecodedFrames > 0 && camera.IsPlaying, camera.PlaybackMessage + "\n" + media.Diagnostics);
            var player = camera.NativePlayer!;
            Require(player.Mute, "Video was not initially muted");
            player.Volume = 0; camera.Muted = false; await Wait(() => !player.Mute, 3000); camera.Muted = true; await Wait(() => player.Mute, 3000);
            var view = (IntegratedContro.App.CameraView)window.FindName("CameraWorkspace");
            var list = (ListBox)view.FindName("CameraList");
            var selected = camera.Selected;
            var row = list.ItemContainerGenerator.ContainerFromItem(selected);
            var resets = 0; var polls = 0;
            var flickering = new HashSet<string>();
            System.Collections.Specialized.NotifyCollectionChangedEventHandler onCollection = (_, e) =>
            { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
            System.ComponentModel.PropertyChangedEventHandler onPoll = (_, e) =>
            { if (e.PropertyName == nameof(camera.AppliedMedia)) polls++; };
            DependencyPropertyChangedEventHandler onEnabled = (sender, _) =>
            { var button = (Button)sender; if (!button.IsEnabled) flickering.Add(button.Content?.ToString() ?? button.Name); };
            var buttons = FindAll<Button>(view).Where(b => b.IsVisible && b.IsEnabled).ToArray();
            camera.FilteredCameras.CollectionChanged += onCollection; camera.PropertyChanged += onPoll;
            foreach (var button in buttons) button.IsEnabledChanged += onEnabled;
            try
            {
                await Wait(() => polls >= 2 && !camera.IsBusy, 10000);
                Require(flickering.Count == 0, "Playback buttons blinked: " + string.Join(", ", flickering));
                Require(resets == 0 && ReferenceEquals(selected, list.SelectedItem) &&
                    ReferenceEquals(row, list.ItemContainerGenerator.ContainerFromItem(selected)),
                    "Background refresh rebuilt the list or lost selected row");
            }
            finally
            {
                foreach (var button in buttons) button.IsEnabledChanged -= onEnabled;
                camera.FilteredCameras.CollectionChanged -= onCollection; camera.PropertyChanged -= onPoll;
            }
            await CameraExecute(camera, camera.RefreshCommand);
            Require(ReferenceEquals(selected, camera.Selected) && ReferenceEquals(player, camera.NativePlayer),
                "Catalog refresh replaced the selection or stopped active playback");
            var screenshot = Path.Combine(output, "camera-native-frame.png");
            Require(player.TakeSnapshot(0, screenshot, 640, 360), "Native snapshot not accepted");
            await Wait(() => File.Exists(screenshot), 5000);
            Capture(window, Path.Combine(output, "camera-workspace.png"));
            var frames = camera.DecodedFrames;
            await media.StopPublisher();
            await Wait(() => camera.PlaybackMessage.Contains("재연결"), 35000);
            media.StartPublisher();
            await Wait(() => camera.PlaybackMessage.Contains("실시간 영상 재생 중") && camera.DecodedFrames > frames, 45000);
            window.WindowState = WindowState.Minimized;
            await Wait(() => !camera.IsPlaying, 15000);
            Require(camera.NativePlayer is null, "Minimize retained native player");
            window.WindowState = WindowState.Normal;
            await Wait(() => camera.CanPlay, 10000); await CameraExecute(camera, camera.PlayCommand);
            await Wait(() => camera.PlaybackMessage.Contains("실시간 영상 재생 중") && camera.DecodedFrames > 0, 30000);
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;
            await Wait(() => !camera.IsPlaying, 15000);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("CameraTab");
            await Wait(() => camera.CanPlay, 10000);
            await CameraExecute(camera, camera.PlayCommand);
            await Wait(() => camera.IsPlaying);
            await Execute(vm, vm.LogoutCommand);
            await Wait(() => !camera.IsPlaying && camera.Cameras.Count == 0);
            Require(camera.NativePlayer is null, "Logout retained video");
            Require(!bindingLog.ToString().Contains("Error:", StringComparison.Ordinal), bindingLog.ToString());
            await File.WriteAllTextAsync(Path.Combine(output, "camera-result.txt"),
                $"PASS: real loopback RTSP -> MediaMTX 1.21.0 -> authenticated host HLS -> LibVLC 3.0.23\nH.264/AAC, decoded frames {frames}, mute, reconnect, catalog refresh, minimize, tab exit, logout.\nNo physical camera or field server used.\n");
        }
        catch (Exception e) { throw new InvalidOperationException(camera.Message + "\n" + camera.PlaybackMessage + "\n" + media.Diagnostics, e); }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            await vm.CloseAsync(); window.Close(); await Task.Delay(100);
        }
    }
    private static async Task RunPreview()
    {
        await using var host = new HostProcess(); await host.Initialize();
        await using var server = new FakeHiperwallServer();
        server.Contents = server.Contents.Replace("type=\"image\"", "type=\"stream\"");
        server.Instances = server.Instances.Replace("type=\"image\"", "type=\"stream\"");
        var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgr32, null, Enumerable.Repeat((byte)120, 64 * 32 * 4).ToArray(), 64 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
        var previews = 0; var fail = false;
        server.Handler = request =>
        {
            if (request.Body.Contains("type=\"preview\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref previews);
                return Task.FromResult(fail ? new FakeHiperwallServer.Response("", 404) :
                    new FakeHiperwallServer.Response("") { Bytes = png, ContentType = "image/png" });
            }
            return Task.FromResult(server.Default(request));
        };
        var window = new MainWindow(false) { Width = 1180, Height = 860 };
        var vm = (MainViewModel)window.DataContext;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint; vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            var hw = vm.Hiperwall;
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HiperwallTab");
            await Wait(() => hw.CanEdit);
            hw.Name = "프리뷰 검증"; hw.Endpoint = server.Endpoint; hw.User = "3"; hw.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            hw.SaveCommand.Execute(null); await Wait(() => !hw.IsBusy);
            await Execute(vm, vm.RefreshCommand); await Wait(() => hw.RefreshCommand.CanExecute(null));
            hw.RefreshCommand.Execute(null); await Wait(() => !hw.IsBusy && hw.Instances.Count == 2);
            var view = (IntegratedContro.App.HiperwallView)window.FindName("HiperwallInventory");
            await view.Previews.Tick();
            await Wait(() => view.Previews.CacheCount == 1 && view.Previews.ActiveRequests == 0);
            Require(previews == 1, "Same-content instances did not share one preview request");
            Require(view.Previews.CacheBytes is > 0 and <= 24 * 1024 * 1024, "Preview cache bound");
            var canvas = (HiperwallCanvas)view.FindName("WallCanvas");
            var prior = canvas.Preview!(hw.Instances[0])!.Image;
            Capture(window, Path.Combine(host.Root, "artifacts", "ui-smoke", "wall-preview.png"));
            fail = true;
            await Wait(() => canvas.Preview!(hw.Instances[0])?.Failed == true, 7000);
            Require(ReferenceEquals(prior, canvas.Preview!(hw.Instances[0])!.Image) && hw.CanEdit,
                "Preview failure discarded the prior image or disabled editing");
            Capture(window, Path.Combine(host.Root, "artifacts", "ui-smoke", "wall-preview-failure.png"));
            window.WindowState = WindowState.Minimized;
            await Wait(() => view.Previews.CacheCount == 0 && view.Previews.ActiveRequests == 0);
            var count = previews; await Task.Delay(1200); Require(previews == count, "Hidden preview request");
            window.WindowState = WindowState.Normal; await view.Previews.Tick();
            await Wait(() => previews > count && view.Previews.ActiveRequests == 0);
            await Execute(vm, vm.LogoutCommand); Require(view.Previews.CacheCount == 0, "Logout retained preview cache");
            await File.WriteAllTextAsync(Path.Combine(host.Root, "artifacts", "ui-smoke", "preview-result.txt"),
                "PASS: shared per-content images, visible-only requests, cache bounds, failure retains prior image and editing, minimize and logout cleanup.\n");
        }
        finally { await vm.CloseAsync(); window.Close(); await Task.Delay(100); }
    }
}
