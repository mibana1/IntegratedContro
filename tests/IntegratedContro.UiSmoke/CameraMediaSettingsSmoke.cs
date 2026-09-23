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
    private static async Task RunCameraMediaSettings()
    {
        await using var host = new HostProcess(); await host.Initialize(120);
        await using var otherHost = new HostProcess(); await otherHost.Initialize(120);
        var (_, ownerLogin) = await host.Login();
        var (_, observerLogin) = await host.Login();
        var (_, otherLogin) = await otherHost.Login();
        using var owner = new HostClient(host.Endpoint, host.Fingerprint); owner.SetToken(ownerLogin.Token);
        using var observer = new HostClient(host.Endpoint, host.Fingerprint); observer.SetToken(observerLogin.Token);
        using var other = new HostClient(otherHost.Endpoint, otherHost.Fingerprint); other.SetToken(otherLogin.Token);
        var lease = await owner.Post<Lease>("/api/lease/acquire");
        var camera = new CameraViewModel(new FakeContentLookup(), _ => throw new InvalidOperationException("No playback in settings test"))
        { Generation = lease.Generation };
        var reader = new CameraViewModel(new FakeContentLookup(), _ => throw new InvalidOperationException("No playback in settings test"));
        var view = new IntegratedContro.App.CameraView { DataContext = camera };
        var window = new Window { Content = view, Width = 1180, Height = 860 };
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindings = new StringWriter(); using var listener = new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        async Task<MediaSettingsView> Read() => (await owner.Get<CameraCatalog>("/api/cameras")).Settings;
        async Task<MediaSettingsView> Change(int version, int port) => await owner.Post<MediaSettingsView>("/api/media/settings",
            new SaveMediaSettingsRequest(lease.Generation, version, $"http://127.0.0.1:{port}", "http://127.0.0.1:18888", "fixture-api", "fixture-reader", "fixture-api-password", "fixture-hls-password"));
        try
        {
            camera.UpdateContext(owner, ownerLogin.Session.Id, true, true, 0);
            window.Show(); ((TabControl)view.FindName("CameraEditorTabs")).SelectedIndex = 1;
            await Wait(() => camera.CanManageMedia && !camera.IsBusy);
            Require(!camera.HasMediaSettings && camera.MediaStatus.Contains("설정 필요") && !camera.IsMediaEditorOpen,
                "Missing configuration was treated as configured or opened the legacy form");
            Capture(window, Path.Combine(output, "camera-media-unconfigured.png"));
            var editor = (Expander)view.FindName("MediaSettingsEditor");
            editor.SetCurrentValue(Expander.IsExpandedProperty, true);
            Require(camera.IsMediaEditorOpen, "Advanced editor binding did not open");
            camera.ApiEndpoint = "http://127.0.0.1:19997"; camera.HlsEndpoint = "http://127.0.0.1:18888";
            camera.ApiUser = "fixture-api"; camera.HlsUser = "fixture-reader";
            ((PasswordBox)view.FindName("ApiPasswordInput")).Password = "fixture-api-password";
            ((PasswordBox)view.FindName("HlsPasswordInput")).Password = "fixture-hls-password";
            await CameraExecute(camera, camera.SaveSettingsCommand);
            var saved = await Read();
            Require(saved.Version == 1 && camera.HasMediaSettings && !camera.MediaDraftConflict, "External configuration was not saved");
            editor.SetCurrentValue(Expander.IsExpandedProperty, false);
            await CameraExecute(camera, camera.RefreshCommand);
            Require((await Read()) == saved, "Automatic settings read wrote host configuration");

            reader.UpdateContext(observer, observerLogin.Session.Id, false, true, saved.Version);
            view.DataContext = reader;
            await Wait(() => reader.HasMediaSettings && !reader.IsBusy);
            Require(reader.ApiEndpoint == saved.ApiEndpoint && reader.ApiUser == saved.ApiUser &&
                !reader.CanManageMedia && !editor.IsEnabled && !reader.SaveSettingsCommand.CanExecute(null),
                "Observer did not automatically use host configuration without edit access");
            Require(((PasswordBox)view.FindName("ApiPasswordInput")).Password == "" &&
                ((PasswordBox)view.FindName("HlsPasswordInput")).Password == "" &&
                !reader.AppliedMedia.Contains("fixture-api-password"), "Credentials were copied into the observer UI");
            Capture(window, Path.Combine(output, "camera-media-observer.png"));
            var updated = await Change(saved.Version, 19998);
            await Wait(() => reader.ApiEndpoint == updated.ApiEndpoint);
            Require((await Read()) == updated, "Observer polling changed shared settings");

            view.DataContext = camera;
            await Wait(() => camera.ApiEndpoint == updated.ApiEndpoint && !camera.IsBusy);
            editor.SetCurrentValue(Expander.IsExpandedProperty, true);
            camera.ApiEndpoint = "http://127.0.0.1:19999";
            ((PasswordBox)view.FindName("HlsPasswordInput")).Password = "unsaved-media-password";
            ((PasswordBox)view.FindName("RtspInput")).Password = "unsaved-camera-source";
            await CameraExecute(camera, camera.RefreshCommand);
            Require(camera.ApiEndpoint.EndsWith(":19999") && ((PasswordBox)view.FindName("HlsPasswordInput")).Password == "unsaved-media-password",
                "Polling overwrote the advanced draft or password");
            updated = await Change(updated.Version, 20000);
            await Wait(() => camera.MediaDraftConflict && !camera.IsBusy);
            Require(camera.ApiEndpoint.EndsWith(":19999") && !camera.SaveSettingsCommand.CanExecute(null),
                "A newer host configuration overwrote the draft or allowed a stale save");
            Capture(window, Path.Combine(output, "camera-media-conflict.png"));
            editor.SetCurrentValue(Expander.IsExpandedProperty, false);
            Require(camera.ApiEndpoint == updated.ApiEndpoint && ((PasswordBox)view.FindName("HlsPasswordInput")).Password == "" &&
                ((PasswordBox)view.FindName("RtspInput")).Password == "unsaved-camera-source", "Cancelling media editing did not isolate secrets from the camera draft");

            owner.SetToken("invalid-test-token"); await CameraExecute(camera, camera.RefreshCommand);
            Require(camera.MediaStatus.Contains("조회 실패") && !camera.CanManageMedia && camera.ApiEndpoint == updated.ApiEndpoint,
                "Failed settings read was presented as success or replaced the stored endpoint");
            owner.SetToken(ownerLogin.Token);
            await Wait(() => camera.CanManageMedia && !camera.IsBusy);
            camera.UpdateContext(owner, ownerLogin.Session.Id, true, true, updated.Version, connected: false);
            Require(camera.MediaStatus.Contains("연결 대기") && !camera.CanManageMedia, "Disconnected host still appeared connected");
            camera.UpdateContext(owner, ownerLogin.Session.Id, true, true, updated.Version);
            await Wait(() => camera.CanManageMedia && !camera.IsBusy);
            camera.UpdateContext(other, otherLogin.Session.Id, false, true, 0);
            Require(camera.ApiEndpoint == "" && !camera.HasMediaSettings && !camera.IsMediaEditorOpen,
                "Switching host retained the old host's editor/configuration");
            await Wait(() => camera.MediaStatus.Contains("설정 필요") && !camera.IsBusy);
            Require(!camera.HasMediaSettings && camera.AppliedMedia == "", "New host inherited the previous host's settings");
            camera.UpdateContext(null, null, false, false, 0);
            Require(camera.ApiEndpoint == "" && !camera.HasMediaSettings && ((PasswordBox)view.FindName("RtspInput")).Password == "",
                "Logout retained another session's data");
            Require(bindings.ToString().Length == 0, "WPF binding errors: " + bindings);
            await File.WriteAllTextAsync(Path.Combine(output, "camera-media-settings-result.txt"),
                "PASS: automatic host settings in two independent authenticated app sessions; observer reads without lease; no media credentials copied; manual external setup retained; read-only polling; draft/password preservation; stale edit fencing; isolated secret clearing; read failure and reconnect; host switch and logout; WPF bindings. Isolated loopback HTTPS hosts, not physical two-PC LAN validation.\n");
            Console.WriteLine("PASS: automatic shared media settings, observer, advanced editing, conflict, reconnect and host switch.");
        }
        finally
        {
            await camera.CloseAsync(); await reader.CloseAsync(); window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
