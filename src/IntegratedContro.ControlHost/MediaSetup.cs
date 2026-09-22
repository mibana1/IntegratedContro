using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.ControlHost;

// Local, stopped-host provisioning. Runtime settings remain owned by ControlHost.
[SupportedOSPlatform("windows")]
internal static class MediaSetup
{
    private sealed record Setup(int Version, int ApiPort, int HlsPort, int RtspPort, Guid CredentialId);
    public static async Task InitializeAsync(string[] args, string dataPath)
    {
        var executable = HostSetup.Option(args, "--media-exe");
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("MediaMTX 실행 파일이 없습니다. App·ControlHost·MediaMTX가 함께 있는 배포본을 사용하세요.");
        var api = Port(args, "--api-port", 9997);
        var hls = Port(args, "--hls-port", 8888);
        var rtsp = Port(args, "--rtsp-port", 8554);
        if (new[] { api, hls, rtsp }.Distinct().Count() != 3)
            throw new ArgumentException("API·HLS·RTSP 포트는 서로 달라야 합니다.");

        // Holds the existing DB's ownership lock through verification and commit.
        using var storage = HostAdapters.OpenStorage(dataPath);
        var state = storage.State.Load();
        var host = JsonSerializer.Deserialize<HostConfiguration>(File.ReadAllText(Path.Combine(storage.DataPath, "host.json")), JsonDefaults.Options)
            ?? throw new InvalidDataException("호스트 설정을 확인하세요.");
        if (new[] { api, hls, rtsp }.Contains(host.Port))
            throw new ArgumentException("영상 서버 포트는 ControlHost HTTPS 포트와 달라야 합니다.");
        var folder = Path.Combine(storage.DataPath, "MediaMTX");
        var manifestPath = Path.Combine(folder, "setup.json");
        var configPath = Path.Combine(folder, "mediamtx.yml");
        if (Directory.Exists(folder)) LocalHostDataPath.Validate(folder, false);
        RejectLink(manifestPath); RejectLink(configPath);
        var secrets = HostAdapters.MediaCredentials(storage.DataPath);
        Setup? saved = null;
        if (File.Exists(manifestPath))
        {
            using var file = File.OpenRead(manifestPath);
            if (file.Length > 4096) throw new InvalidDataException("영상 서버 자동 설정 기록을 확인하세요.");
            saved = JsonSerializer.Deserialize<Setup>(file, JsonDefaults.Options)
                ?? throw new InvalidDataException("영상 서버 자동 설정 기록을 확인하세요.");
            if (saved.Version != 1 || saved.CredentialId == Guid.Empty ||
                saved.ApiPort != api || saved.HlsPort != hls || saved.RtspPort != rtsp)
                throw new InvalidDataException("이 폴더의 자동 설정 포트와 다릅니다. 기존 포트로 다시 확인하거나 기존 설정 파일 사용을 선택하세요.");
        }
        else if (state.Media is not null || File.Exists(folder) ||
                 Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("기존 영상 설정이 있습니다. 자동 생성으로 덮어쓰지 않습니다. 기존 설정 파일 사용을 선택하세요.");

        MediaConfiguration Configuration(Setup setup) => new(1, $"http://127.0.0.1:{api}", $"http://127.0.0.1:{hls}",
            "integrated-api", "integrated-hls", setup.CredentialId);
        if (saved is not null && state.Media is not null && state.Media != Configuration(saved))
            throw new InvalidOperationException("ControlHost의 영상 설정이 변경되었습니다. 기존 설정 파일 사용을 선택하세요.");
        if (state.Media is null && state.Cameras.Count + state.CameraCleanup.Count > 0)
            throw new InvalidOperationException("기존 카메라와 경로 정리 기록을 먼저 확인하세요.");

        // Never adopt or stop an existing listener, even if it is another MediaMTX.
        CheckPorts(api, hls, rtsp);
        if (saved is null)
        {
            var credentials = new MediaCredentials(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            var reference = secrets.Save(JsonSerializer.Serialize(credentials, JsonDefaults.Options));
            saved = new(1, api, hls, rtsp, reference);
            try
            {
                Directory.CreateDirectory(folder);
                CreateFile(manifestPath, JsonSerializer.Serialize(saved, JsonDefaults.Options));
            }
            catch
            {
                // If a completed record exists, preserve its recovery reference.
                if (!File.Exists(manifestPath)) secrets.Delete(reference);
                throw;
            }
        }
        var passwords = JsonSerializer.Deserialize<MediaCredentials>(secrets.Read(saved.CredentialId), JsonDefaults.Options);
        if (passwords is null || passwords.ApiPassword?.Length != 64 || passwords.HlsPassword?.Length != 64 ||
            passwords.ApiPassword == passwords.HlsPassword)
            throw new InvalidDataException("영상 서버 보호 계정 정보를 확인하세요.");
        var yaml = ConfigurationText(saved, passwords);
        if (File.Exists(configPath))
        {
            if (new FileInfo(configPath).Length > 16384 || File.ReadAllText(configPath) != yaml)
                throw new InvalidDataException("자동 생성한 MediaMTX 설정 파일이 변경되었습니다. 덮어쓰지 않으며 기존 설정 파일 사용에서 확인하세요.");
        }
        else CreateFile(configPath, yaml); // Explicit retry uses the same protected credentials.

        var configuration = Configuration(saved);
        await MediaSetupProbe.VerifyAsync(executable, configPath, configuration, passwords);
        if (state.Media is null)
        {
            state.Media = configuration;
            state.Audit.Add(new(DateTimeOffset.UtcNow, null, "MediaSettingsSaved", "version=1 · 로컬 영상 서버 최초 설정 및 API/HLS 인증 확인"));
            storage.State.Save(state);
        }
        Console.WriteLine("영상 서버 설정·전용 계정 등록 및 API/HLS 인증 연결 확인 완료. 카메라 영상 입력은 등록 후 별도로 확인하세요.");
    }
    private static int Port(string[] args, string key, int fallback)
    {
        if (!int.TryParse(HostSetup.Option(args, key) ?? fallback.ToString(), out var port) || port is < 1024 or > 65535)
            throw new ArgumentException("영상 서버 포트는 1024~65535 정수로 입력하세요.");
        return port;
    }
    private static void RejectLink(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("링크로 연결된 영상 설정 파일은 사용할 수 없습니다.");
    }
    private static void CheckPorts(params int[] ports)
    {
        var listeners = new List<TcpListener>();
        try
        {
            foreach (var port in ports)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listeners.Add(listener);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
            }
        }
        catch (SocketException)
        { throw new InvalidOperationException("영상 서버 포트를 이미 사용 중이거나 사용할 수 없습니다. 실행 중인 서버를 정상 종료하거나 다른 포트를 선택하세요."); }
        finally { foreach (var listener in listeners) listener.Stop(); }
    }
    private static void CreateFile(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(Encoding.UTF8.GetBytes(text)); file.Flush(true); }
            File.Move(temporary, path); // Do not replace an existing config or recovery record.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // MediaMTX 1.21 supports base64 SHA-256 credentials. Generated passwords have 256 bits of entropy.
    private static string Hash(string password) => "sha256:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    private static string ConfigurationText(Setup setup, MediaCredentials credentials) => $"""
        # Generated by IntegratedContro. Keep with the host data and protected credentials.
        logLevel: warn
        logDestinations: [stdout]
        api: yes
        apiAddress: 127.0.0.1:{setup.ApiPort}
        rtsp: yes
        rtspAddress: 127.0.0.1:{setup.RtspPort}
        rtspTransports: [tcp]
        rtmp: no
        webrtc: no
        srt: no
        moq: no
        hls: yes
        hlsAddress: 127.0.0.1:{setup.HlsPort}
        hlsVariant: mpegts
        hlsSegmentCount: 4
        hlsSegmentDuration: 1s
        hlsSegmentMaxSize: 32M
        hlsMuxerCloseAfter: 10s
        hlsDirectory: ''
        authMethod: internal
        authInternalUsers:
          - user: integrated-api
            pass: '{Hash(credentials.ApiPassword)}'
            ips: ['127.0.0.1']
            permissions:
              - action: api
          - user: integrated-hls
            pass: '{Hash(credentials.HlsPassword)}'
            ips: ['127.0.0.1']
            permissions:
              - action: read
                path: '~^ic-'
        """ + "\npaths: {}\n";
}
