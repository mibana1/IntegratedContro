using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunServerStartup()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var exe = host.Process!.MainModule!.FileName;
        await host.Kill();
        var folder = Path.Combine(host.DataPath, "startup fixture with spaces"); Directory.CreateDirectory(folder);
        var media = Path.Combine(folder, "mediamtx.exe");
        File.Copy(Path.Combine(host.Root, "artifacts", "media-tools", "mediamtx", "mediamtx.exe"), media);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var mediaUri = new Uri($"http://127.0.0.1:{port}");
        var config = Path.Combine(folder, "media.yml");
        await File.WriteAllTextAsync(config, $"""
            logLevel: warn
            logDestinations: [file]
            logFile: mediamtx.log
            api: yes
            apiAddress: 127.0.0.1:{port}
            rtsp: no
            rtmp: no
            hls: no
            webrtc: no
            srt: no
            """);
        var settingsPath = Path.Combine(folder, "startup.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new LocalServerStartupSettings(host.DataPath, media, config, mediaUri.ToString())
            { ControlHostExecutablePath = exe }, JsonDefaults.Options));
        var profile = Path.Combine(folder, "profile"); Directory.CreateDirectory(profile);
        new ClientPreferences(Guid.NewGuid(), host.Endpoint, host.Fingerprint).Save(Path.Combine(profile, "client.json"));
        int? hostPid = null, mediaPid = null;
        var external = new LocalServerLifetime();
        try
        {
            await Child(expectPrompt: true, decline: true);
            Require(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)) is null && WindowsServerProcesses.ListenerProcess(mediaUri) is null,
                "Declining launched servers");
            await Child(expectPrompt: true);
            Require(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)) is null && WindowsServerProcesses.ListenerProcess(mediaUri) is null,
                "The owner app left its servers running after closing");
            await LocalServerStartup.StartAsync(settingsPath, host.Root, new(new(Guid.NewGuid(), host.Endpoint, host.Fingerprint)), _ => Task.FromResult(true), external);
            hostPid = WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)); mediaPid = WindowsServerProcesses.ListenerProcess(mediaUri);
            Require(hostPid is not null && mediaPid is not null, "Cold startup did not start both servers");
            await Child(expectPrompt: false);
            Require(hostPid == WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)) && mediaPid == WindowsServerProcesses.ListenerProcess(mediaUri),
                "Warm app launch replaced or duplicated a server");
            await StopOwned(mediaPid); mediaPid = null;
            await Child(expectPrompt: true);
            mediaPid = WindowsServerProcesses.ListenerProcess(mediaUri);
            Require(mediaPid is null && hostPid == WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)), "Media-only owner did not stop only MediaMTX");
            await LocalServerStartup.StartAsync(settingsPath, host.Root, new(new(Guid.NewGuid(), host.Endpoint, host.Fingerprint)), _ => Task.FromResult(true), external);
            mediaPid = WindowsServerProcesses.ListenerProcess(mediaUri);
            await StopOwned(hostPid); hostPid = null;
            await Child(expectPrompt: true);
            hostPid = WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint));
            Require(hostPid is null && mediaPid == WindowsServerProcesses.ListenerProcess(mediaUri), "Host-only owner did not stop only ControlHost");
            await LocalServerStartup.StartAsync(settingsPath, host.Root, new(new(Guid.NewGuid(), host.Endpoint, host.Fingerprint)), _ => Task.FromResult(true), external);
            var login = await host.Login(); Require(login.Login.Token.Length > 0, "Original accounts were lost");
            var result = "PASS: production App prompt/decline, cold start of two real EXEs, no prompt or PID change on warm start, one-server start, owner UI closes its servers; other UI preserves existing servers, existing account retained.";
            await File.WriteAllTextAsync(Path.Combine(host.Root, "artifacts", "ui-smoke", "server-startup-result.txt"), result);
            Console.WriteLine(result);
        }
        finally
        {
            await external.StopAsync();
            await StopOwned(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
            await StopOwned(WindowsServerProcesses.ListenerProcess(mediaUri));
        }
        async Task Child(bool expectPrompt, bool decline = false)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--profile-dir", profile, "--server-startup", settingsPath, "--server-startup-child" }) start.ArgumentList.Add(arg);
            if (expectPrompt) start.ArgumentList.Add("--expect-prompt");
            if (decline) start.ArgumentList.Add("--decline");
            using var child = Process.Start(start)!;
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(child.ExitCode == 0, $"Production startup child failed: prompt={expectPrompt}, decline={decline}");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        static async Task StopOwned(int? pid)
        {
            if (pid is null) return;
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
            catch (ArgumentException) { /* Already removed from the process table. */ }
        }
    }

    private static int RunServerStartupChild(string[] args)
    {
        var app = new IntegratedContro.App.App(); app.InitializeComponent();
        var sawPrompt = false; var finished = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var elapsed = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            try
            {
                var dialog = FindStartupDialog("#32770", "서버 실행");
                if (dialog != IntPtr.Zero)
                {
                    GetStartupDialogProcess(dialog, out var pid);
                    if (pid == Environment.ProcessId)
                    {
                        sawPrompt = true;
                        SendStartupDialogMessage(dialog, 0x111, new IntPtr(args.Contains("--decline") ? 7 : 6), IntPtr.Zero);
                    }
                }
                if (!finished && app.MainWindow is MainWindow { LoginDialog.IsVisible: true } window)
                {
                    Require(sawPrompt == args.Contains("--expect-prompt"), "Unexpected/missing startup question");
                    var message = ((MainViewModel)window.DataContext).Message;
                    Require(args.Contains("--decline") ? message.Contains("건너뛰었습니다") :
                        message.Contains("MediaMTX") && message.Contains("ControlHost") && !message.Contains(":"), "Unexpected readiness: " + message);
                    finished = true; timer.Stop(); window.LoginDialog!.Close();
                }
                if (elapsed.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Startup UI timed out");
            }
            catch (Exception error) { Console.Error.WriteLine(error); timer.Stop(); app.Shutdown(1); }
        };
        timer.Start(); return app.Run();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindStartupDialog(string className, string title);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetStartupDialogProcess(IntPtr window, out int processId);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendStartupDialogMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
