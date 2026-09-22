using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.UiSmoke;

internal static class MediaSetupLifecycle
{
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(50); }
    }
    private static int[] Ports()
    {
        var listeners = Enumerable.Range(0, 4).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var listener in listeners) listener.Start();
        var result = listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        foreach (var listener in listeners) listener.Stop();
        return result;
    }
    public static async Task Run()
    {
        var repo = Directory.GetParent(Directory.GetParent(Directory.GetParent(AppContext.BaseDirectory)!.FullName)!.FullName)!.FullName;
        while (!File.Exists(Path.Combine(repo, "IntegratedContro.sln"))) repo = Directory.GetParent(repo)!.FullName;
        var root = Path.GetDirectoryName(ClientPreferences.ProfilePath)!;
        Directory.CreateDirectory(root);
        var config = new StartupConfiguration(Path.Combine(root, "profile"), SetupTestPaths.App(Path.Combine(root, "install", "App")));
        var profilePath = Path.Combine(root, "profile", "client.json");
        var data = Path.Combine(root, "영상 서버 데이터");
        var hostExe = Path.Combine(repo, "src", "IntegratedContro.ControlHost", "bin", "Debug", "net10.0", "win-x64", "IntegratedContro.ControlHost.exe");
        var mediaExe = Path.Combine(repo, "artifacts", "media-tools", "mediamtx", "mediamtx.exe");
        hostExe = SetupTestPaths.Host(hostExe); mediaExe = SetupTestPaths.Media(mediaExe);
        config.Save(new(data, mediaExe, "", "") { ControlHostExecutablePath = hostExe, MediaMtxEnabled = false });
        var ports = Ports();
        var password = "한글-" + Guid.NewGuid().ToString("N");
        var window = new InitialSetupWindow(config, profilePath);
        var vm = window.Model;
        vm.ModeIndex = 0; vm.DataPath = data; vm.SiteName = "영상 자동 설정 확인"; vm.Administrator = "media-admin";
        vm.Port = ports[0].ToString();
        vm.MediaEnabled = true; vm.MediaModeIndex = 0;
        vm.MediaApiPort = ports[1].ToString(); vm.MediaHlsPort = ports[2].ToString(); vm.MediaRtspPort = ports[3].ToString();
        ((PasswordBox)window.FindName("AdminPassword")).Password = password;
        ((PasswordBox)window.FindName("ConfirmPassword")).Password = password;
        using var bindingLog = new StringWriter();
        using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var shown = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => window.ShowDialog());
        await Wait(() => window.IsVisible);
        ((Expander)window.FindName("MediaSetupSection")).IsExpanded = true;
        window.UpdateLayout();
        ((TextBox)window.FindName("MediaApiPortInput")).BringIntoView();
        await Task.Delay(150);
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        using (var file = File.Create(Path.Combine(root, "media-setup.png")))
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file); }
        Require(((TextBox)window.FindName("MediaApiPortInput")).IsVisible, "Automatic port input hidden");
        vm.MediaModeIndex = 1; window.UpdateLayout();
        Require(!((TextBox)window.FindName("MediaApiPortInput")).IsVisible, "Existing mode still exposes automatic fields");
        vm.MediaModeIndex = 0;
        vm.SaveCommand.Execute(null);
        await Wait(() => !vm.IsBusy);
        Require(!window.IsVisible, vm.Message);
        Require(await shown == true, "Setup did not complete");
        Require(bindingLog.ToString().Length == 0, "WPF binding errors");
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        var profile = ClientPreferences.ReadForStartup(profilePath);
        var yamlPath = Path.Combine(data, "MediaMTX", "mediamtx.yml");
        var yaml = File.ReadAllText(yamlPath);
        HostState before;
        using (var storage = SqliteHostStorage.Open(data)) before = storage.State.Load();
        var media = before.Media ?? throw new InvalidOperationException("Media settings not saved");
        var secrets = new MediaCredentialStore(data, new WindowsCurrentUserSecretProtector());
        var credentials = JsonSerializer.Deserialize<MediaCredentials>(secrets.Read(media.CredentialId), JsonDefaults.Options)!;
        Require(credentials.ApiPassword != credentials.HlsPassword, "Shared password");
        Require(!yaml.Contains(credentials.ApiPassword) && !yaml.Contains(credentials.HlsPassword), "Plaintext YAML");
        Require(yaml.Contains("sha256:"), "Hashed credentials missing");
        Require(profile.Preferences.LastLoginName == "media-admin", "Admin login name not saved");
        Require(config.Read()!.MediaMtxConfigurationPath == yamlPath, "Startup file not registered");
        Console.WriteLine("PASS WPF automatic setup, protected separate credentials, DB/startup/client registration and hidden verification child cleanup");

        var service = new InitialSetupService(config, profilePath);
        async Task Retry() => await service.SaveLocalAsync(data, false, "", "", "", "", "", "", true, "", "", profile.Preferences,
            true, ports[1].ToString(), ports[2].ToString(), ports[3].ToString());
        await Retry();
        using (var storage = SqliteHostStorage.Open(data))
        {
            var after = storage.State.Load();
            Require(after.SiteId == before.SiteId && after.Media == before.Media && after.Audit.Count == before.Audit.Count,
                "Retry changed identity, media secrets or history");
        }
        Require(File.ReadAllText(yamlPath) == yaml, "Retry changed YAML");
        File.AppendAllText(yamlPath, "# edited\n");
        var blocked = false;
        try { await Retry(); } catch (InvalidOperationException) { blocked = true; }
        Require(blocked && File.ReadAllText(yamlPath).EndsWith("# edited\n"), "Edited config overwritten");
        File.WriteAllText(yamlPath, yaml);
        using (var occupied = new TcpListener(IPAddress.Loopback, ports[2]))
        {
            occupied.Start(); blocked = false;
            try { await Retry(); } catch (InvalidOperationException) { blocked = true; }
            Require(blocked && occupied.Server.IsBound, "Existing listener was adopted/stopped");
        }
        Console.WriteLine("PASS idempotent retry, edited config preservation, occupied port preservation");

        var lifetime = new LocalServerLifetime();
        try
        {
            var startup = await LocalServerStartup.StartAsync(config.SettingsPath, config.AppDirectory, profile,
                _ => Task.FromResult(true), lifetime);
            Require(startup.Contains("ControlHost 시작 완료") && startup.Contains("MediaMTX 시작 완료"), startup);
            var expectedPin = Convert.FromHexString(profile.Preferences.Fingerprint);
            using var http = new HttpClient(new HttpClientHandler
            { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), expectedPin) })
            { BaseAddress = new Uri(profile.Preferences.Endpoint) };
            using var response = await http.PostAsJsonAsync("/api/login", new LoginRequest("media-admin", password, profile.Preferences.PcId, Environment.MachineName));
            Require(response.IsSuccessStatusCode, "Created administrator cannot log in");
            var login = await response.Content.ReadFromJsonAsync<LoginResult>(JsonDefaults.Options);
            http.DefaultRequestHeaders.Authorization = new("Bearer", login!.Token);
            var catalog = await http.GetFromJsonAsync<CameraCatalog>("/api/cameras", JsonDefaults.Options);
            Require(catalog!.Settings.ApiEndpoint == media.ApiEndpoint && catalog.Settings.HasSecret, "Host did not expose generated media registration");
            Console.WriteLine("PASS saved settings start both servers; administrator login and MediaMTX registration readback through HTTPS");
        }
        finally { await lifetime.StopAsync(); }

        var reload = new InitialSetupViewModel(config, profilePath);
        Require(reload.IsAutomaticMedia && reload.MediaApiPort == ports[1].ToString() &&
            reload.MediaHlsPort == ports[2].ToString(), "Saved automatic ports not restored");
        var failureConfig = new StartupConfiguration(Path.Combine(root, "failure-profile"), SetupTestPaths.App(Path.Combine(root, "install", "App")));
        var failureProfile = Path.Combine(root, "failure-profile", "client.json");
        var failureData = Path.Combine(root, "실패 복구 데이터");
        failureConfig.Save(new(failureData, hostExe, "", "") { ControlHostExecutablePath = hostExe, MediaMtxEnabled = false });
        var failurePreferences = ClientPreferences.ReadForStartup(failureProfile).Preferences;
        var failureService = new InitialSetupService(failureConfig, failureProfile);
        var rejected = false;
        try
        {
            await failureService.SaveLocalAsync(failureData, true, "실패 복구", "retry-admin", password, password,
                "127.0.0.1", ports[0].ToString(), true, "", "", failurePreferences, true,
                ports[1].ToString(), ports[2].ToString(), ports[3].ToString());
        }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Invalid media executable was accepted");
        using (var storage = SqliteHostStorage.Open(failureData))
            Require(storage.State.Load().Media is null, "Failed probe registered settings as successful");
        var recoveryYaml = File.ReadAllText(Path.Combine(failureData, "MediaMTX", "mediamtx.yml"));
        var recoveryRecord = File.ReadAllText(Path.Combine(failureData, "MediaMTX", "setup.json"));
        failureConfig.Save(failureConfig.Read()! with { MediaMtxExecutablePath = mediaExe });
        await failureService.SaveLocalAsync(failureData, false, "", "", "", "", "", "", true, "", "",
            failurePreferences, true, ports[1].ToString(), ports[2].ToString(), ports[3].ToString());
        Require(recoveryYaml == File.ReadAllText(Path.Combine(failureData, "MediaMTX", "mediamtx.yml")) &&
            recoveryRecord == File.ReadAllText(Path.Combine(failureData, "MediaMTX", "setup.json")),
            "Failure recovery regenerated credentials/config");
        using (var storage = SqliteHostStorage.Open(failureData))
            Require(storage.State.Load().Media is not null, "Retry did not complete registration");
        Console.WriteLine("PASS failed verification stays unregistered, retry preserves generated credentials, saved ports reload");

        Console.WriteLine("EVIDENCE " + root);
    }
}
