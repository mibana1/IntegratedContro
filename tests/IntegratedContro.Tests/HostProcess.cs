using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.Testing;

public sealed class HostProcess : IAsyncDisposable
{
    public string Root { get; }
    public string DataPath { get; }
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string Endpoint { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public Process? Process { get; private set; }
    private readonly List<HttpClient> _clients = []; private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logs = new();
    public HostProcess()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IntegratedContro.sln"))) root = root.Parent;
        Root = root?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        DataPath = Path.Combine(Root, "artifacts", "process-tests", Guid.NewGuid().ToString());
    }
    private string HostExe => Path.Combine(Root, "src", "IntegratedContro.ControlHost", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0", "win-x64", "IntegratedContro.ControlHost.exe");
    private Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(HostExe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Host failed to start");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _logs.Enqueue(e.Data); }; process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logs.Enqueue(e.Data); }; process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return process;
    }
    public async Task Initialize(int heartbeatSeconds = 15)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var setup = Start("setup", "--data", DataPath, "--bind", "127.0.0.1", "--port", port.ToString(),
            "--heartbeat-timeout", heartbeatSeconds.ToString(), "--site", "로컬 가상 검증", "--admin", "admin");
        await setup.StandardInput.WriteLineAsync(Password); await setup.StandardInput.WriteLineAsync(Password);
        setup.StandardInput.Close(); await setup.WaitForExitAsync();
        if (setup.ExitCode != 0) throw new InvalidOperationException("Isolated setup failed");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(DataPath, "host.json")));
        Fingerprint = config.RootElement.GetProperty("certificateSha256").GetString()!;
        Endpoint = $"https://127.0.0.1:{port}";
        await Run();
    }
    public async Task Run()
    {
        Process = Start("run", "--data", DataPath);
        using var client = NewClient();
        string lastError = ""; for (var i = 0; i < 100; i++)
        {
            if (Process.HasExited) throw new InvalidOperationException("Host exited during startup");
            try { using var response = await client.GetAsync("/health"); if (response.IsSuccessStatusCode) return; lastError = response.StatusCode.ToString(); } catch (HttpRequestException error) { lastError = error.ToString(); }
            await Task.Delay(50);
        }
        throw new TimeoutException("Host readiness timeout: " + lastError + "\n" + string.Join("\n", _logs));
    }
    public HttpClient NewClient(string? token = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var expected = Convert.FromHexString(Fingerprint);
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
            DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), expected);
        var client = new HttpClient(handler) { BaseAddress = new Uri(Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _clients.Add(client); return client;
    }
    public async Task<(HttpClient Client, LoginResult Login)> Login(string user = "admin")
    {
        var client = NewClient();
        var login = await Post<LoginResult>(client, "/api/login", new LoginRequest(user, Password, Guid.NewGuid(), Environment.MachineName));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return (client, login);
    }
    public static async Task<T> Post<T>(HttpClient client, string route, object? body = null)
    {
        using var response = body is null ? await client.PostAsync(route, null) : await client.PostAsJsonAsync(route, body, JsonDefaults.Options);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{route}: {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options))!;
    }
    public static async Task<StateView> State(HttpClient client) =>
        (await client.GetFromJsonAsync<StateView>("/api/state", JsonDefaults.Options))!;
    public static async Task<StateView> Until(HttpClient client, Func<StateView, bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var state = await State(client);
            if (condition(state)) return state;
            await Task.Delay(100);
        }
        throw new TimeoutException("Expected host state was not reached");
    }
    public async Task Kill()
    {
        if (Process is not null)
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            Process.Dispose(); Process = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) client.Dispose();
        await Kill();
        // Keep isolated evidence on disk under artifacts; no operational directory is changed or deleted.
    }
}
