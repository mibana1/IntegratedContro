using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class LocalServerStartupTests
{
    [Fact]
    public async Task Concurrent_apps_start_one_host_with_existing_data_and_reuse_it()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var exe = host.Process!.MainModule!.FileName;
        await host.Kill();
        var settings = WriteSettings(host, exe);
        var profile = Profile(host);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => LocalServerStartup.StartAsync(settings, host.Root, profile, Approve)));
        var pid = WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint));
        Assert.NotNull(pid);
        using var process = Process.GetProcessById(pid.Value);
        try
        {
            Assert.Single(results, r => r.Contains("ControlHost 시작 완료"));
            Assert.True(results.Count(r => r.Contains("ControlHost 실행 중")) == 2, string.Join("\n", results));
            Assert.All(results, r => Assert.Contains("MediaMTX:", r)); // Missing media does not block control.
            var login = await host.Login(); Assert.NotNull(login.Login.Token); // Original account survived.
            var reused = await LocalServerStartup.StartAsync(settings, host.Root, profile, Approve);
            Assert.Contains("ControlHost 실행 중", reused);
            Assert.Equal(pid, WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
            Assert.False(process.HasExited); // No caller owns/stops this independent process.
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task Remote_different_and_damaged_profiles_do_not_start_local_workers()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName);
        await host.Kill();
        var profile = Profile(host);
        Assert.Equal("", await LocalServerStartup.StartAsync(settings, host.Root, profile with
        { Preferences = profile.Preferences with { Endpoint = "https://192.0.2.1:7443" } }, Approve));
        Assert.Equal("", await LocalServerStartup.StartAsync(settings, host.Root, profile with
        { Preferences = profile.Preferences with { Fingerprint = new string('A', 64) } }, Approve));
        Assert.Contains("복구", await LocalServerStartup.StartAsync(settings, host.Root, profile with { RecoveryRequired = true }, Approve));
        Assert.Null(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
        Assert.False(File.Exists(Path.Combine(host.DataPath, "app-server-startup.lock")));
    }

    [Fact]
    public async Task Missing_database_is_not_initialized_or_replaced()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName);
        await host.Kill();
        var database = Path.Combine(host.DataPath, "control.sqlite");
        File.Move(database, database + ".original");
        var result = await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), Approve);
        Assert.Contains("control.sqlite", result);
        Assert.False(File.Exists(database));
        Assert.Null(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
    }

    [Fact]
    public async Task Busy_host_port_does_not_launch_or_kill_a_process()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, "missing-host.exe");
        await host.Kill();
        using var listener = new TcpListener(IPAddress.Loopback, new Uri(host.Endpoint).Port);
        listener.Start();
        var result = await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), Approve, timeout: TimeSpan.FromMilliseconds(200));
        Assert.Contains("ControlHost: 응답 대기", result);
        Assert.Equal(Environment.ProcessId, WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
    }

    [Fact]
    public async Task Another_process_on_media_port_is_not_reused_or_stopped()
    {
        await using var host = new HostProcess(); await host.Initialize();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var mediaEndpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName, mediaEndpoint);
        var pid = host.Process.Id;
        var result = await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), Approve);
        Assert.Contains("다른 프로세스", result); Assert.Contains("ControlHost 실행 중", result);
        Assert.Equal(Environment.ProcessId, WindowsServerProcesses.ListenerProcess(new Uri(mediaEndpoint)));
        Assert.Equal(pid, WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
    }

    [Fact]
    public async Task Invalid_or_disabled_startup_settings_keep_client_available()
    {
        await using var host = new HostProcess();
        Directory.CreateDirectory(host.DataPath);
        var path = Path.Combine(host.DataPath, "startup.json");
        var profile = new ClientPreferencesLoadResult(new(Guid.NewGuid(), "https://127.0.0.1:7443", new string('A', 64)));
        Assert.Equal("", await LocalServerStartup.StartAsync(path, host.Root, profile, Approve));
        await File.WriteAllTextAsync(path, "{ broken");
        Assert.Contains("JSON", await LocalServerStartup.StartAsync(path, host.Root, profile, Approve));
        await File.WriteAllTextAsync(path, "{\"enabled\":false}");
        Assert.Equal("", await LocalServerStartup.StartAsync(path, host.Root, profile, Approve));
    }

    private static Task<bool> Approve(IReadOnlyList<string> servers) => Task.FromResult(true);
    [Fact]
    public async Task Stopped_servers_require_approval_and_declining_does_not_launch_them()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName);
        await host.Kill();
        IReadOnlyList<string>? asked = null;
        var result = await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), names =>
        {
            asked = names; return Task.FromResult(false);
        });
        Assert.NotNull(asked); Assert.Equal(2, asked.Count);
        Assert.Contains("건너뛰었습니다", result);
        Assert.Null(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
    }

    [Fact]
    public async Task Running_host_is_not_in_the_confirmation_for_stopped_media()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName);
        IReadOnlyList<string>? asked = null;
        await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), names =>
        {
            asked = names; return Task.FromResult(false);
        });
        Assert.NotNull(asked); Assert.Single(asked); Assert.StartsWith("MediaMTX", asked[0]);
        Assert.False(host.Process.HasExited);
    }

    [Fact]
    public async Task Owner_shutdown_stops_its_host_gracefully_and_reused_host_is_preserved()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName);
        var reused = new LocalServerLifetime();
        await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), Approve, reused);
        await reused.StopAsync();
        Assert.False(host.Process.HasExited);
        await host.Kill();
        var owner = new LocalServerLifetime();
        try
        {
            Assert.Contains("ControlHost 시작 완료", await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), Approve, owner));
            using var process = Process.GetProcessById(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint))!.Value);
            _ = process.Handle; // Retain the native handle so exit status survives process removal.
            await owner.StopAsync();
            Assert.True(process.HasExited); Assert.Equal(0, process.ExitCode);
            Assert.Null(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
        }
        finally { await owner.StopAsync(); }
    }

    [Fact]
    public async Task Closing_during_confirmation_prevents_late_server_launch()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var settings = WriteSettings(host, host.Process!.MainModule!.FileName);
        await host.Kill();
        var lifetime = new LocalServerLifetime();
        await LocalServerStartup.StartAsync(settings, host.Root, Profile(host), async _ =>
        {
            await lifetime.StopAsync(); return true;
        }, lifetime);
        Assert.Null(WindowsServerProcesses.ListenerProcess(new Uri(host.Endpoint)));
    }

    private static ClientPreferencesLoadResult Profile(HostProcess host) => new(new(Guid.NewGuid(), host.Endpoint, host.Fingerprint));
    private static string WriteSettings(HostProcess host, string exe, string? mediaEndpoint = null)
    {
        if (mediaEndpoint is null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            mediaEndpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        }
        var path = Path.Combine(host.DataPath, "startup.json");
        var mediaConfig = Path.Combine(host.DataPath, "media.yml"); File.WriteAllText(mediaConfig, "api: yes");
        var settings = new LocalServerStartupSettings(host.DataPath, Path.Combine(host.DataPath, "missing-media.exe"), mediaConfig, mediaEndpoint)
            { ControlHostExecutablePath = exe };
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonDefaults.Options));
        return path;
    }
}
