using System.Diagnostics;
using System.IO;
using System.Text;

namespace IntegratedContro.App;

// The host owns setup and validation; the UI never opens or creates the DB.
public sealed class InitialSetupService(StartupConfiguration configuration, string profilePath)
{
    public async Task SaveLocalAsync(string dataPath, bool create, string site, string administrator,
        string password, string confirmation, string bind, string port, bool mediaEnabled, string mediaConfig,
        string mediaApi, ClientPreferences profile, bool initializeMedia = false,
        string mediaApiPort = "9997", string mediaHlsPort = "8888", string mediaRtspPort = "8554")
    {
        var data = configuration.ValidateDataPath(dataPath.Trim(), create);
        var settings = new LocalServerStartupSettings(data, "../MediaMTX/mediamtx.exe", mediaConfig.Trim(), mediaApi.Trim())
        { MediaMtxEnabled = mediaEnabled };
        LocalServerStartupSettings? current = null;
        try { var saved = configuration.Read(); if (saved is not null) StartupConfiguration.ValidateSettings(saved); current = saved; }
        catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e)) { /* Explicit repair preserves damaged settings. */ }
        if (current is { Enabled: true })
        {
            EnsureStopped(current.HostDataPath);
            settings = settings with { ControlHostExecutablePath = current.ControlHostExecutablePath,
                MediaMtxExecutablePath = string.IsNullOrWhiteSpace(current.MediaMtxExecutablePath) ? settings.MediaMtxExecutablePath : current.MediaMtxExecutablePath };
        }
        initializeMedia &= mediaEnabled;
        if (initializeMedia)
        {
            var ports = new[] { mediaApiPort, mediaHlsPort, mediaRtspPort };
            if (ports.Any(p => !int.TryParse(p, out var n) || n is < 1024 or > 65535) ||
                ports.Select(int.Parse).Distinct().Count() != 3)
                throw new ArgumentException("API·HLS·RTSP 포트는 1024~65535 사이의 서로 다른 정수로 입력하세요.");
            var mediaExecutable = Path.GetFullPath(settings.MediaMtxExecutablePath, configuration.AppDirectory);
            if (!File.Exists(mediaExecutable)) throw new FileNotFoundException("MediaMTX 실행 파일이 없습니다. MediaMTX가 포함된 배포본을 사용하세요.");
            settings = settings with { MediaMtxConfigurationPath = Path.Combine(data, "MediaMTX", "mediamtx.yml"),
                MediaMtxApiEndpoint = $"http://127.0.0.1:{int.Parse(mediaApiPort)}" };
        }
        StartupConfiguration.ValidateSettings(settings);
        if (mediaEnabled && !initializeMedia && !File.Exists(settings.MediaMtxConfigurationPath)) throw new InvalidDataException("선택한 MediaMTX 설정 파일이 없습니다.");
        var executable = Path.GetFullPath(settings.ControlHostExecutablePath, configuration.AppDirectory);
        if (create)
        {
            if (string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(administrator)) throw new ArgumentException("현장 이름과 최초 관리자 아이디를 입력하세요.");
            if (password.Length < 12 || password != confirmation || password.Contains('\n') || password.Contains('\r'))
                throw new ArgumentException("비밀번호는 12자 이상이어야 하며 확인 값과 같아야 합니다.");
            if (!System.Net.IPAddress.TryParse(bind, out _) || !int.TryParse(port, out var number) || number is < 1024 or > 65535)
                throw new ArgumentException("수신 IP와 포트(1024~65535)를 확인하세요.");
            await RunHost(executable, ["setup", "--data", data, "--site", site.Trim(), "--admin", administrator.Trim(), "--bind", bind, "--port", port], password);
        }
        else
        {
            _ = StartupConfiguration.ReadHost(data);
            EnsureStopped(data);
            await RunHost(executable, ["inspect", "--data", data]);
        }
        if (initializeMedia)
            await RunHost(executable, ["setup-media", "--data", data,
                "--media-exe", Path.GetFullPath(settings.MediaMtxExecutablePath, configuration.AppDirectory),
                "--api-port", mediaApiPort, "--hls-port", mediaHlsPort, "--rtsp-port", mediaRtspPort]);
        var metadata = StartupConfiguration.ReadHost(data);
        var updated = profile with { Endpoint = metadata.Endpoint, Fingerprint = metadata.CertificateSha256,
            LastLoginName = create ? administrator.Trim() : profile.LastLoginName };
        configuration.Save(settings);
        updated.Save(profilePath);
    }

    public void SaveRemote(string endpoint, string fingerprint, ClientPreferences profile)
    {
        endpoint = endpoint.Trim(); fingerprint = fingerprint.Replace(" ", "").Replace(":", "").Trim().ToUpperInvariant();
        _ = ClientPreferences.ValidateConnection(endpoint, fingerprint);
        configuration.Save(new("", "", "", "") { Enabled = false, MediaMtxEnabled = false });
        (profile with { Endpoint = endpoint, Fingerprint = fingerprint }).Save(profilePath);
    }

    private static void EnsureStopped(string data)
    {
        if (Directory.Exists(data) && WindowsServerProcesses.HostIsLocked(data))
            throw new InvalidOperationException("로컬 서버가 실행 중입니다. 진행 작업을 확인하고 서버를 정상 종료한 뒤 데이터 위치를 변경하세요.");
    }
    private static async Task RunHost(string executable, string[] arguments, string? password = null)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("제어 서버 실행 파일이 없습니다. App과 ControlHost가 함께 있는 배포본을 사용하세요.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--utf8");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("제어 서버 설정을 시작하지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (password is not null)
        { await process.StandardInput.WriteLineAsync(password); await process.StandardInput.WriteLineAsync(password); }
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); // Our setup child and its temporary media check only.
            await process.WaitForExitAsync();
            throw new InvalidOperationException("호스트 설정 시간이 초과됐습니다. 선택한 폴더를 점검하세요. 자동으로 다시 초기화하지 않습니다.");
        }
        _ = await output;
        var failure = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("호스트 설정 확인 실패: " + failure.Trim());
    }
}
