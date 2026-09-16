using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace IntegratedContro.UiSmoke;

// A generated test pattern over real loopback RTSP / MediaMTX / HLS. Never connects to field cameras.
internal sealed class NativeMediaFixture : IAsyncDisposable
{
    private readonly string _root, _config, _ffmpeg, _mediaMtx;
    private readonly ConcurrentQueue<string> _logs = new();
    private Process? _server, _publisher;
    public int RtspPort { get; } = Port();
    public int ApiPort { get; } = Port();
    public int HlsPort { get; } = Port();
    public string ApiEndpoint => $"http://127.0.0.1:{ApiPort}";
    public string HlsEndpoint => $"http://127.0.0.1:{HlsPort}";
    public string Source => $"rtsp://127.0.0.1:{RtspPort}/origin";
    public const string ApiPassword = "fixture-api-only";
    public const string HlsPassword = "fixture-hls-only";
    public const string RtspPassword = "fixture-rtsp-only";
    public string Diagnostics => string.Join("\n", _logs.TakeLast(30));
    public NativeMediaFixture(string root)
    {
        _root = root;
        _mediaMtx = Path.Combine(root, "artifacts", "media-tools", "mediamtx", "mediamtx.exe");
        var ffmpegRoot = Path.Combine(root, "artifacts", "media-tools", "ffmpeg");
        _ffmpeg = Directory.Exists(ffmpegRoot) ? Directory.GetFiles(ffmpegRoot, "ffmpeg.exe", SearchOption.AllDirectories).Single() : "";
        if (!File.Exists(_mediaMtx) || !File.Exists(_ffmpeg)) throw new FileNotFoundException("Run scripts/prepare-media-tests.ps1 first.");
        var folder = Path.Combine(root, "artifacts", "media-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        _config = Path.Combine(folder, "mediamtx.yml");
        File.WriteAllText(_config, $"""
            logLevel: warn
            logDestinations: [stdout]
            api: yes
            apiAddress: 127.0.0.1:{ApiPort}
            rtsp: yes
            rtspAddress: 127.0.0.1:{RtspPort}
            rtspTransports: [tcp]
            rtmp: no
            webrtc: no
            srt: no
            moq: no
            hls: yes
            hlsAddress: 127.0.0.1:{HlsPort}
            hlsVariant: mpegts
            hlsSegmentCount: 4
            hlsSegmentDuration: 1s
            hlsMuxerCloseAfter: 5s
            hlsDirectory: ''
            authMethod: internal
            authInternalUsers:
              - user: api
                pass: {ApiPassword}
                ips: ['127.0.0.1']
                permissions:
                  - action: api
              - user: reader
                pass: {HlsPassword}
                ips: ['127.0.0.1']
                permissions:
                  - action: read
              - user: camera
                pass: {RtspPassword}
                ips: ['127.0.0.1']
                permissions:
                  - action: read
                    path: origin
                  - action: publish
                    path: origin
            paths:
              origin:
                source: publisher
            """ + "\n", new UTF8Encoding(false));
    }
    private static int Port() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
    private Process Start(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var p = Process.Start(info) ?? throw new InvalidOperationException("Fixture process failed.");
        void Capture(object sender, DataReceivedEventArgs e) { if (e.Data is not null) _logs.Enqueue(e.Data); while (_logs.Count > 100) _logs.TryDequeue(out _); }
        p.OutputDataReceived += Capture; p.ErrorDataReceived += Capture;
        p.BeginOutputReadLine(); p.BeginErrorReadLine(); return p;
    }
    public async Task StartAsync()
    {
        _server = Start(_mediaMtx, _config);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + ApiPassword)));
        for (var i = 0; i < 100; i++)
        {
            if (_server.HasExited) throw new InvalidOperationException("MediaMTX fixture: " + Diagnostics);
            try { using var response = await client.GetAsync(ApiEndpoint + "/v3/paths/list"); if (response.IsSuccessStatusCode) { StartPublisher(); return; } }
            catch (HttpRequestException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException("MediaMTX fixture startup: " + Diagnostics);
    }
    public void StartPublisher()
    {
        _publisher = Start(_ffmpeg, "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=20",
            "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100", "-c:v", "libx264", "-preset", "ultrafast",
            "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", "20", "-c:a", "aac", "-f", "rtsp", "-rtsp_transport", "tcp",
            $"rtsp://camera:{RtspPassword}@127.0.0.1:{RtspPort}/origin");
    }
    public async Task StopPublisher()
    {
        if (_publisher is null) return;
        if (!_publisher.HasExited) { _publisher.Kill(true); await _publisher.WaitForExitAsync(); }
        _publisher.Dispose(); _publisher = null;
    }
    public async ValueTask DisposeAsync()
    {
        await StopPublisher();
        if (_server is not null)
        {
            if (!_server.HasExited) { _server.Kill(true); await _server.WaitForExitAsync(); }
            _server.Dispose();
        }
    }
}
