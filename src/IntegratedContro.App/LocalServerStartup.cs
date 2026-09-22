using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

// Deployment paths only; the host continues to own the DB and protected credentials.
public sealed record LocalServerStartupSettings(string HostDataPath, string MediaMtxExecutablePath,
    string MediaMtxConfigurationPath, string MediaMtxApiEndpoint)
{
    public bool Enabled { get; init; } = true;
    public bool MediaMtxEnabled { get; init; } = true;
    public string ControlHostExecutablePath { get; init; } = "../ControlHost/IntegratedContro.ControlHost.exe";
}

public static class LocalServerStartup
{
    public const string FileName = "server-startup.json";
    private sealed record HostSettings(string BindAddress, int Port, string CertificateSha256);

    public static Task<string> StartForAppAsync(Func<IReadOnlyList<string>, Task<bool>> confirmStart, LocalServerLifetime lifetime) => Task.Run(async () =>
    {
        try
        {
            var configuration = StartupConfiguration.ForApp();
            _ = configuration.Read();
            return await StartAsync(configuration.SettingsPath, AppContext.BaseDirectory, ClientPreferences.ReadForStartup(), confirmStart, lifetime);
        }
        catch (Exception error) when (StartupConfiguration.IsConfigurationFailure(error))
        { return "서버 설정 오류: " + StartupConfiguration.FriendlyError(error) + " 초기 설정 · 저장 위치에서 확인하세요."; }
    });

    public static async Task<string> StartAsync(string settingsPath, string appDirectory,
        ClientPreferencesLoadResult profile, Func<IReadOnlyList<string>, Task<bool>> confirmStart, LocalServerLifetime? lifetime = null, TimeSpan? timeout = null)
    {
        try
        {
            if (!File.Exists(settingsPath)) return ""; // Client-only installations have no startup settings.
            var settings = Read<LocalServerStartupSettings>(settingsPath);
            if (!settings.Enabled) return "";
            if (profile.RecoveryRequired) return "접속 설정을 복구한 뒤 앱을 다시 열면 로컬 서버를 시작합니다.";
            if (!Uri.TryCreate(profile.Preferences.Endpoint, UriKind.Absolute, out var endpoint) || !IsLocal(endpoint)) return "";
            var data = AbsoluteLocalPath(settings.HostDataPath);
            var config = Read<HostSettings>(Path.Combine(data, "host.json"));
            // A different profile must never start this site's workers as a connection fallback.
            var (_, pin) = ClientPreferences.ValidateConnection(profile.Preferences.Endpoint, profile.Preferences.Fingerprint);
            if (endpoint.Port != config.Port || !string.Equals(Convert.ToHexString(pin), config.CertificateSha256, StringComparison.OrdinalIgnoreCase)) return "";
            if (!IPAddress.TryParse(config.BindAddress, out var bind) || config.Port is < 1024 or > 65535 ||
                !(bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any) ||
                  endpoint.IsLoopback && IPAddress.IsLoopback(bind) || endpoint.Host == bind.ToString()))
                throw new InvalidDataException("접속 주소와 기존 host.json의 수신 주소를 확인하세요.");
            RequireFile(Path.Combine(data, "control.sqlite"));
            var hostExe = Path.GetFullPath(settings.ControlHostExecutablePath, appDirectory);
            var wait = timeout ?? TimeSpan.FromSeconds(15);
            // Ask once, and only for stopped servers. Never hold the startup lock while the user decides.
            var stopped = new List<string>();
            using (await WindowsServerProcesses.AcquireStartupLock(data, wait))
            {
                if (settings.MediaMtxEnabled && Uri.TryCreate(settings.MediaMtxApiEndpoint, UriKind.Absolute, out var mediaCheck) &&
                    mediaCheck.Scheme == "http" && mediaCheck.IsLoopback &&
                    WindowsServerProcesses.ListenerProcess(mediaCheck) is null)
                    stopped.Add("MediaMTX (카메라 영상 서버)");
                if (!await ProbeHost(endpoint, pin) && WindowsServerProcesses.ListenerProcess(endpoint) is null &&
                    !WindowsServerProcesses.HostIsLocked(data))
                    stopped.Add("ControlHost (제어 서버)");
            }
            if (stopped.Count > 0 && !await confirmStart(stopped))
                return "서버 실행을 건너뛰었습니다. 서버가 준비된 뒤 접속하거나 앱을 다시 열어 실행하세요.";
            // Recheck under a cross-process/cross-session lock after approval: another app may have started them.
            using var startupLock = await WindowsServerProcesses.AcquireStartupLock(data, wait);
            var messages = new List<string>();
            if (settings.MediaMtxEnabled) try
            {
                var mediaExe = Path.GetFullPath(settings.MediaMtxExecutablePath, appDirectory);
                var mediaConfig = AbsoluteLocalPath(settings.MediaMtxConfigurationPath);
                if (!Uri.TryCreate(settings.MediaMtxApiEndpoint, UriKind.Absolute, out var mediaUri) ||
                    mediaUri.Scheme != "http" || !mediaUri.IsLoopback || mediaUri.AbsolutePath != "/" ||
                    mediaUri.UserInfo != "" || mediaUri.Query != "" || mediaUri.Fragment != "")
                    throw new InvalidDataException("MediaMTX API는 이 PC의 http loopback 주소로 설정하세요.");
                RequireFile(mediaConfig);
                messages.Add(await EnsureServer("MediaMTX", mediaExe, [mediaConfig], Path.GetDirectoryName(mediaConfig)!,
                    mediaUri, () => ProbeMedia(mediaUri), wait, stopped.Any(s => s.StartsWith("MediaMTX", StringComparison.Ordinal)), lifetime, expectedExecutable: mediaExe));
            }
            catch (Exception error) when (ExpectedFailure(error)) { messages.Add("MediaMTX: " + FriendlyError(error)); }
            try
            {
                messages.Add(await EnsureServer("ControlHost", hostExe, ["run", "--data", data], data,
                    endpoint, () => ProbeHost(endpoint, pin), wait, stopped.Any(s => s.StartsWith("ControlHost", StringComparison.Ordinal)), lifetime, hostData: data));
            }
            catch (Exception error) when (ExpectedFailure(error)) { messages.Add("ControlHost: " + FriendlyError(error)); }
            return string.Join(" · ", messages);
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            return "서버 시작 확인: " + FriendlyError(error) + " 접속 설정과 server-startup.json을 확인하세요.";
        }
    }

    private static async Task<string> EnsureServer(string name, string executable, string[] arguments,
        string directory, Uri endpoint, Func<Task<bool>> probe, TimeSpan timeout, bool approvedToStart, LocalServerLifetime? lifetime,
        string? expectedExecutable = null, string? hostData = null)
    {
        using var deadline = new CancellationTokenSource(timeout);
        System.Diagnostics.Process? started = null;
        try
        {
            // A running older host stays untouched, even if the new binary cannot be found.
            if (await IsReady()) return name + " 실행 중";
            var owner = WindowsServerProcesses.ListenerProcess(endpoint);
            if (expectedExecutable is not null && owner is not null && !WindowsServerProcesses.IsExecutable(owner.Value, expectedExecutable))
                throw new InvalidDataException("설정한 포트를 다른 프로세스가 사용 중입니다. 기존 프로세스는 유지합니다.");
            if (owner is null && (hostData is null || !WindowsServerProcesses.HostIsLocked(hostData)))
            {
                if (!approvedToStart) return name + ": 서버가 종료됐습니다. 앱을 다시 열어 실행 여부를 선택하세요.";
                RequireFile(executable);
                started = lifetime is null ? WindowsServerProcesses.Start(executable, arguments, directory) : lifetime.Start(executable, arguments, directory);
            }
            while (true)
            {
                if (await IsReady()) return name + (started is null ? " 실행 중" : " 시작 완료");
                if (started?.HasExited == true)
                    throw new InvalidDataException("서버가 시작 도중 종료됐습니다. 기존 설정·포트·데이터 접근 권한을 확인하세요.");
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) { return name + ": 응답 대기 시간이 지났습니다. 서버 설정·포트·실행 상태를 확인하세요."; }
        finally { if (lifetime is null) started?.Dispose(); } // Owned handles are kept until app shutdown.

        async Task<bool> IsReady()
        {
            if (expectedExecutable is not null)
            {
                var pid = WindowsServerProcesses.ListenerProcess(endpoint);
                if (pid is null || !WindowsServerProcesses.IsExecutable(pid.Value, expectedExecutable)) return false;
            }
            return await probe();
        }
    }

    private static async Task<bool> ProbeHost(Uri endpoint, byte[] pin)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
            DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), pin);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 4096 };
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/health"));
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ready" &&
                json.RootElement.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out var version) && version == 1;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return false; }
    }

    private static async Task<bool> ProbeMedia(Uri endpoint)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 4096 };
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/v3/config/global/get"), HttpCompletionOption.ResponseHeadersRead);
            // 401 from the authenticated API is expected; do not copy API or camera credentials.
            return (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized) &&
                response.Headers.Server.Any(value => string.Equals(value.Product?.Name, "mediamtx", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return false; }
    }

    internal static bool IsLocal(Uri uri) => uri.IsLoopback || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) &&
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(address));
    private static T Read<T>(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 64 * 1024) throw new InvalidDataException("시작 설정 파일이 너무 큽니다.");
        return JsonSerializer.Deserialize<T>(file, JsonDefaults.Options) ?? throw new InvalidDataException("시작 설정이 비어 있습니다.");
    }
    private static string AbsoluteLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.StartsWith("//"))
            throw new InvalidDataException("기존 로컬 파일/폴더의 절대 경로가 필요합니다.");
        return Path.GetFullPath(path);
    }
    private static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"필요한 파일이 없습니다: {Path.GetFileName(path)}. 기존 경로를 확인하세요.");
    }
    private static bool ExpectedFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException;
    private static string FriendlyError(Exception error) => error switch
    {
        InvalidDataException => error.Message,
        JsonException => "시작 설정 JSON을 읽지 못했습니다.",
        TimeoutException => "다른 앱의 서버 시작을 기다리는 시간이 지났습니다.",
        _ => "기존 실행 파일·설정 경로·접근 권한을 확인하세요."
    };
}
