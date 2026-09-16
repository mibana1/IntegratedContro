using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

/// <summary>MediaMTX v3 API, only app-owned immutable paths; redirects never forward credentials.</summary>
public sealed class MediaMtxHttpClient : IMediaMtxClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Cookie, DateTimeOffset At)> _hlsCookies = new();
    private readonly Timer _cookieCleanup;
    private void PruneCookies()
    {
        foreach (var entry in _hlsCookies.Where(e => DateTimeOffset.UtcNow - e.Value.At > TimeSpan.FromSeconds(60)).ToArray())
            _hlsCookies.TryRemove(entry.Key, out _);
    }
    public MediaMtxHttpClient(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        _cookieCleanup = new Timer(_ => PruneCookies(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
    private async Task<HttpResponseMessage> Send(MediaConfiguration c, MediaCredentials credentials,
        HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, c.ApiEndpoint + route);
        request.Headers.Authorization = Basic(c.ApiUser, credentials.ApiPassword);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
    private static void Check(HttpResponseMessage response, bool missing = false)
    {
        if (response.IsSuccessStatusCode || missing && response.StatusCode == HttpStatusCode.NotFound) return;
        throw new DomainException("mediamtx_failed",
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "MediaMTX 전용 계정의 인증·권한을 확인하세요." : $"MediaMTX 요청 실패 (HTTP {(int)response.StatusCode}).", 502);
    }
    public async Task EnsurePathAsync(MediaConfiguration c, MediaCredentials credentials, string path, string source, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(path);
        using var current = await Send(c, credentials, HttpMethod.Get, "/v3/config/paths/get/" + encoded, null, ct);
        Check(current, true);
        var body = new { source, sourceOnDemand = false, rtspTransport = "tcp" };
        if (current.StatusCode != HttpStatusCode.NotFound)
        {
            using var json = JsonDocument.Parse(await ReadBounded(current, 128 * 1024, ct));
            var root = json.RootElement;
            if (root.TryGetProperty("source", out var src) && src.GetString() == source &&
                root.TryGetProperty("sourceOnDemand", out var demand) && demand.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("rtspTransport", out var transport) && transport.GetString() == "tcp") return;
        }
        using var result = await Send(c, credentials, current.StatusCode == HttpStatusCode.NotFound ? HttpMethod.Post : HttpMethod.Patch,
            "/v3/config/paths/" + (current.StatusCode == HttpStatusCode.NotFound ? "add/" : "patch/") + encoded, body, ct);
        Check(result);
    }
    public async Task DeletePathAsync(MediaConfiguration c, MediaCredentials credentials, string path, CancellationToken ct)
    {
        using var response = await Send(c, credentials, HttpMethod.Delete,
            "/v3/config/paths/delete/" + Uri.EscapeDataString(path), null, ct);
        Check(response, true);
    }
    public async Task<CameraConnection> GetStatusAsync(MediaConfiguration c, MediaCredentials credentials, string path, CancellationToken ct)
    {
        using var response = await Send(c, credentials, HttpMethod.Get, "/v3/paths/get/" + Uri.EscapeDataString(path), null, ct);
        Check(response, true);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "경로의 실제 영상 입력을 아직 확인하지 못했습니다.");
        using var json = JsonDocument.Parse(await ReadBounded(response, 128 * 1024, ct));
        var ready = json.RootElement.TryGetProperty("ready", out var value) && value.ValueKind == JsonValueKind.True;
        return new(ready, ready ? "MediaMTX 실제 입력 준비됨 · 영상 재생 가능" : "경로는 저장됨 · 실제 영상 입력 대기");
    }
    public async Task<MediaPayload> ReadHlsAsync(MediaConfiguration c, MediaCredentials credentials, string path, string asset, CancellationToken ct)
    {
        if (!MediaLimits.ValidAsset(asset)) throw MediaLimits.Invalid();
        var url = c.HlsEndpoint + "/" + Uri.EscapeDataString(path) + "/" + asset;
        var key = c.CredentialId.ToString("N") + ":" + path;
        async Task<HttpResponseMessage> Fetch(bool cookieCheck)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url + (cookieCheck ? "?cookieCheck=1" : ""));
            request.Headers.Authorization = Basic(c.HlsUser, credentials.HlsPassword);
            if (cookieCheck) request.Headers.Add("Cookie", "cookieCheck=1");
            else if (_hlsCookies.TryGetValue(key, out var entry))
            {
                _hlsCookies[key] = (entry.Cookie, DateTimeOffset.UtcNow);
                request.Headers.Add("Cookie", entry.Cookie);
            }
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        var response = await Fetch(false);
        try
        {
            // MediaMTX 1.21 negotiates a cookie for each HLS session. Only this exact same-path
            // handshake is followed; no arbitrary redirect or session token reaches the UI/URL.
            if (response.StatusCode == HttpStatusCode.Found && asset == "index.m3u8" &&
                response.Headers.Location is { } location && new Uri(new Uri(url), location).AbsoluteUri == url + "?cookieCheck=1")
            {
                response.Dispose(); response = await Fetch(true);
            }
            Check(response);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var raw in cookies.Take(4))
                {
                    var pair = raw.Split(';', 2)[0].Split('=', 2);
                    if (pair.Length == 2 && pair[0].Length <= 64 && pair[0].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') &&
                        Guid.TryParseExact(pair[1], "D", out var secret))
                    {
                        if (_hlsCookies.Count >= 64)
                            foreach (var old in _hlsCookies.OrderBy(x => x.Value.At).Take(_hlsCookies.Count - 63).ToArray())
                                _hlsCookies.TryRemove(old.Key, out _);
                        _hlsCookies[key] = (pair[0] + "=" + secret.ToString("D"), DateTimeOffset.UtcNow);
                    }
                }
            var playlist = asset.EndsWith(".m3u8", StringComparison.Ordinal);
            var bytes = await ReadBounded(response, playlist ? MediaLimits.PlaylistBytes : MediaLimits.SegmentBytes, ct);
            if (playlist) MediaLimits.ValidatePlaylist(bytes);
            return new(bytes, playlist ? "application/vnd.apple.mpegurl" : asset.EndsWith(".ts", StringComparison.Ordinal) ? "video/mp2t" : "video/mp4");
        }
        finally { response.Dispose(); }
    }
    internal static async Task<byte[]> ReadBounded(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw MediaLimits.Invalid();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (memory.Length + count > limit) throw MediaLimits.Invalid();
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    public void Dispose() { _cookieCleanup.Dispose(); _hlsCookies.Clear(); _http.Dispose(); }
}
