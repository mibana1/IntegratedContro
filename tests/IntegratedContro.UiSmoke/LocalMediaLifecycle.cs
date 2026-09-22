using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IntegratedContro.App;
using CameraView = IntegratedContro.App.CameraView;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.UiSmoke;

internal static class LocalMediaLifecycle
{
    static string Root = "";
    static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    static async Task Wait(Func<bool> condition) { var end = DateTime.UtcNow.AddSeconds(55); while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(50); } }
    static async Task Execute(CameraViewModel vm, AsyncCommand command) { await Wait(() => !vm.IsBusy); Need(command.CanExecute(null), "Camera command disabled: " + vm.Message); command.Execute(null); await Wait(() => !vm.IsBusy); }
    static async Task Execute(MainViewModel vm, AsyncCommand command) { Need(command.CanExecute(null), "Main command disabled: " + vm.Message); command.Execute(null); await Wait(() => !vm.IsBusy); }
    static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Root, name + ".png")); encoder.Save(file);
    }
    public static async Task Run()
    {
        Root = Path.GetDirectoryName(ClientPreferences.ProfilePath)!;
        Directory.CreateDirectory(Root);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "IntegratedContro.sln"))) repo = repo.Parent;
        var hostExe = Path.Combine(repo!.FullName, "src", "IntegratedContro.ControlHost", "bin", "Debug", "net10.0", "win-x64", "IntegratedContro.ControlHost.exe");
        var mediaExe = Path.Combine(repo.FullName, "artifacts", "media-tools", "mediamtx", "mediamtx.exe");
        hostExe = SetupTestPaths.Host(hostExe); mediaExe = SetupTestPaths.Media(mediaExe);
        var config = new StartupConfiguration(Root, SetupTestPaths.App(Path.Combine(Root, "install", "App")));
        var data = Path.Combine(Root, "data");
        var sockets = Enumerable.Range(0, 4).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var s in sockets) s.Start();
        var ports = sockets.Select(s => ((IPEndPoint)s.LocalEndpoint).Port).ToArray(); foreach (var s in sockets) s.Stop();
        config.Save(new(data, mediaExe, "", "") { MediaMtxEnabled = false, ControlHostExecutablePath = hostExe });
        var password = Guid.NewGuid().ToString("N");
        var profile = ClientPreferences.ReadForStartup().Preferences;
        profile = await new InitialSetupService(config, ClientPreferences.ProfilePath).SaveLocalAsync(data, true, "영상 변경 격리 검사", "admin", password, password,
            "127.0.0.1", ports[0].ToString(), true, "", "", profile, true, ports[1].ToString(), ports[2].ToString(), ports[3].ToString());
        // This fixture owns MediaMTX explicitly so a host restart cannot restart the stopped test server.
        config.Save(config.Read()! with { MediaMtxEnabled = false });
        Console.WriteLine("PASS initial managed media setup");
        var yaml = Path.Combine(data, "MediaMTX", "mediamtx.yml");
        var manifest = Path.Combine(data, "MediaMTX", "setup.json");
        // Exercise the original setup.json format before configurationVersion was introduced.
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        legacy.Remove("configurationVersion"); File.WriteAllText(manifest, legacy.ToJsonString());
        Process? mediaProcess = null;
        LocalServerLifetime lifetime = new();
        MainWindow? main = null; Window? window = null;
        HostClient? api = null;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        async Task StartMedia()
        {
            var start = new ProcessStartInfo(mediaExe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(yaml);
            mediaProcess = Process.Start(start)!; mediaProcess.OutputDataReceived += (_, _) => { }; mediaProcess.ErrorDataReceived += (_, _) => { };
            mediaProcess.BeginOutputReadLine(); mediaProcess.BeginErrorReadLine(); await Task.Delay(500);
        }
        async Task StopMedia() { if (mediaProcess is not null) { if (!mediaProcess.HasExited) { mediaProcess.Kill(); await mediaProcess.WaitForExitAsync(); } mediaProcess.Dispose(); mediaProcess = null; } }
        async Task<(MainViewModel Main, CameraViewModel Camera, CameraView View)> Open()
        {
            var started = await LocalServerStartup.StartAsync(config.SettingsPath, config.AppDirectory, new(profile), _ => Task.FromResult(true), lifetime);
            Need(started.Contains("ControlHost"), started);
            main = new MainWindow(showLoginOnStart: false); main.Show();
            var vm = (MainViewModel)main.DataContext; vm.ReadLoginPassword = () => password;
            vm.LoginName = "admin"; await Execute(vm, vm.LoginCommand); Need(vm.IsLoggedIn, vm.Message);
            // A clean normal logout before the restart leaves the lease free.
            await Execute(vm, vm.AcquireCommand); Need(vm.CanConfigure, vm.Message);
            ((TabControl)main.FindName("MainTabs")).SelectedItem = main.FindName("CameraTab");
            var view = (CameraView)main.FindName("CameraWorkspace");
            window = main;
            ((TabControl)view.FindName("CameraEditorTabs")).SelectedIndex = 1;
            vm.Cameras.ConfirmLocalMediaChange = _ => true;
            await Execute(vm.Cameras, vm.Cameras.LoadSettingsCommand);
            Need(vm.Cameras.IsLocalMedia, vm.Cameras.LocalMediaMessage);
            return (vm, vm.Cameras, view);
        }
        async Task<HostClient> Api()
        {
            var result = new HostClient(profile.Endpoint, profile.Fingerprint);
            var login = await result.Post<LoginResult>("/api/login", new LoginRequest("admin", password, Guid.NewGuid(), "isolated observer"));
            result.SetToken(login.Token); return result;
        }
        async Task<MediaSettingsView> Settings() => (await api!.Get<CameraCatalog>("/api/cameras")).Settings;
        async Task CloseWindows()
        {
            window = null; main?.Close(); if (main is not null) await Wait(() => !main.IsVisible); main = null;
        }
        try
        {
            await StartMedia();
            var (vm, camera, view) = await Open();
            api = await Api();
            Need(!camera.CanEditMediaTarget && camera.CanEditMediaPasswords, "Managed targets not protected");
            Capture(window!, "local-media-ready");
            var before = await Settings();
            try
            {
                await api.Post<MediaSettingsView>("/api/media/local/passwords", new ChangeLocalMediaPasswordsRequest(vm.Cameras.Generation, before.Version, "must-not-write-pass", null));
                throw new InvalidOperationException("Observer changed passwords without owning the lease");
            }
            catch (ApiException) { }
            var newApi = "한글-API-" + Guid.NewGuid().ToString("N");
            ((PasswordBox)view.FindName("ApiPasswordInput")).Password = newApi;
            await Execute(camera, camera.SaveSettingsCommand);
            var changed = await Settings();
            Need(changed.LocalServer?.Phase == LocalMediaChangePhase.AwaitingVerification && changed.Version == before.Version + 1, camera.Message);
            Need(((PasswordBox)view.FindName("ApiPasswordInput")).Password == "", "Password input retained");
            Need(!File.ReadAllText(yaml).Contains(newApi) && !File.ReadAllText(manifest).Contains(newApi), "Plain password in files");
            Capture(window!, "local-media-pending");
            await Task.Delay(600);
            await Execute(camera, camera.VerifyLocalMediaCommand);
            Need(!camera.HasLocalMediaChange, camera.Message);
            Need(mediaProcess is { HasExited: false }, "Live reload stopped existing media process");
            Console.WriteLine("PASS WPF local password change, live API/HLS auth, unchanged HLS password, lease fencing, secret clearing");

            // Isolate the deliberate file failure from the live server configuration watcher.
            await StopMedia();
            var yamlBeforeFailure = File.ReadAllText(yaml);
            var manifestBeforeFailure = File.ReadAllText(manifest);
            // Lock manifest replacement after YAML can be replaced: exercise a real partial write.
            var newHls = "HLS-" + Guid.NewGuid().ToString("N");
            using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ((PasswordBox)view.FindName("HlsPasswordInput")).Password = newHls;
                await Execute(camera, camera.SaveSettingsCommand);
                var prepared = await Settings();
                Need(prepared.LocalServer?.Phase == LocalMediaChangePhase.Prepared, "Partial apply not journaled");
                // An unrelated short-lived file handle may reject the YAML replacement first.
                // Exercise explicit user retries while the deliberate manifest lock stays held.
                for (var attempt = 0; File.ReadAllText(yaml) == yamlBeforeFailure && attempt < 10; attempt++)
                {
                    Need(File.ReadAllText(manifest) == manifestBeforeFailure, "Locked manifest changed");
                    Console.WriteLine("RETRY: partial file apply still pending; verify stable journal and unchanged version");
                    await Task.Delay(250);
                    await Execute(camera, camera.RetryLocalMediaCommand);
                    var retry = await Settings();
                    Need(retry.Version == prepared.Version && retry.LocalServer?.ChangeId == prepared.LocalServer!.ChangeId &&
                        retry.LocalServer?.Phase == LocalMediaChangePhase.Prepared && retry.LocalServer.Message.Contains("파일을 읽거나 저장하지 못"),
                        "File failure retry did not preserve the journal/version: " + camera.Message);
                }
                Need(File.ReadAllText(yaml) != yamlBeforeFailure && File.ReadAllText(manifest) == manifestBeforeFailure,
                    "The injected failure did not leave exactly the YAML applied and the manifest unchanged");
                Need(camera.HasLocalMediaChange && !camera.CanEditMediaPasswords, "Failure not recoverable");
            }
            Capture(window!, "local-media-file-failure");
            var pending = (await Settings()).LocalServer!.ChangeId;
            await CloseWindows(); api.Dispose(); api = null;
            await lifetime.StopAsync(); lifetime = new();
            (vm, camera, view) = await Open(); api = await Api();
            Need((await Settings()).LocalServer?.ChangeId == pending, "Host restart lost recovery record");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Execute(camera, camera.RetryLocalMediaCommand);
                var retry = await Settings();
                if (retry.LocalServer?.Phase == LocalMediaChangePhase.AwaitingVerification) break;
                Need(retry.Version == changed.Version && retry.LocalServer?.ChangeId == pending &&
                    retry.LocalServer?.Phase == LocalMediaChangePhase.Prepared && retry.LocalServer.Message.Contains("파일을 읽거나 저장하지 못"),
                    "Restarted retry lost the journal/version: " + camera.Message);
                Console.WriteLine("RETRY: file access still unavailable after restart; recovery journal preserved");
                await Task.Delay(250);
            }
            Need((await Settings()).LocalServer?.Phase == LocalMediaChangePhase.AwaitingVerification, camera.Message);
            await Execute(camera, camera.VerifyLocalMediaCommand);
            Need(camera.HasLocalMediaChange && camera.Message.Contains("확인하지 못"), "Stopped server was called verified");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var current = await Settings();
                Need(current.LocalServer?.ChangeId == pending, "Restore lost the recovery journal");
                await Execute(camera, current.LocalServer!.Phase == LocalMediaChangePhase.Restoring
                    ? camera.RetryLocalMediaCommand : camera.RestoreLocalMediaCommand);
                var restored = await Settings();
                if (restored.LocalServer?.Phase == LocalMediaChangePhase.AwaitingRestoreVerification) break;
                Need(restored.LocalServer?.ChangeId == pending && restored.Version == current.Version &&
                    ((restored.LocalServer!.Phase == LocalMediaChangePhase.Restoring && restored.LocalServer.Message.Contains("파일을 읽거나 저장하지 못")) ||
                     (restored.LocalServer!.Phase == LocalMediaChangePhase.AwaitingVerification && camera.Message.Contains("진행 중"))),
                    "Restore failure did not preserve recoverable state: " + camera.Message);
                Console.WriteLine("RETRY: restore remains recoverable; unchanged change ID and version");
                await Task.Delay(500);
            }
            Need((await Settings()).LocalServer?.Phase == LocalMediaChangePhase.AwaitingRestoreVerification, camera.Message);
            Capture(window!, "local-media-restored");
            await StartMedia(); await Execute(camera, camera.VerifyLocalMediaCommand);
            Need(!camera.HasLocalMediaChange, camera.Message);
            Console.WriteLine("PASS partial file failure, host restart durability, retry, stopped-server verification failure and previous-settings restoration");
            var healthy = File.ReadAllText(yaml);
            File.AppendAllText(yaml, "# external edit\n");
            await Execute(camera, camera.LoadSettingsCommand);
            Need(!camera.CanEditMediaPasswords && File.ReadAllText(yaml).EndsWith("# external edit\n"), "External changes overwritten");
            File.WriteAllText(yaml, healthy);
            Need(bindingLog.ToString().Length == 0, "WPF binding errors: " + bindingLog);
            Console.WriteLine("PASS external edit preservation and WPF bindings; evidence: " + Root);
        }
        finally
        {
            await CloseWindows(); api?.Dispose();
            await lifetime.StopAsync(); await StopMedia();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
        using var storage = SqliteHostStorage.Open(data);
        var final = storage.State.Load();
        Need(final.LocalMediaChange is null, "Verified recovery journal not completed");
        Need(final.Accounts.Count == 1, "Existing accounts changed");
        var secrets = new MediaCredentialStore(data, new WindowsCurrentUserSecretProtector());
        var creds = JsonSerializer.Deserialize<MediaCredentials>(secrets.Read(final.Media!.CredentialId), JsonDefaults.Options)!;
        var stateJson = JsonSerializer.Serialize(final, JsonDefaults.Options);
        Need(!stateJson.Contains(creds.ApiPassword) && !stateJson.Contains(creds.HlsPassword), "Plain credentials in DB state");
        Console.WriteLine("PASS final DB consistency, protected credentials, account preservation");
    }
}
