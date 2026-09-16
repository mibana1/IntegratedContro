using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class ApiException(string code, string message, HttpStatusCode status) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
}
public sealed class HostClient : IDisposable
{
    private readonly HttpClient _http;
    public HostClient(string endpoint, string fingerprint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("https://호스트주소:포트 형식으로 입력하세요.");
        byte[] pin;
        try { pin = Convert.FromHexString(fingerprint.Replace(" ", "").Replace(":", "").Trim()); }
        catch (FormatException) { throw new ArgumentException("호스트 설정에 표시된 SHA-256 지문을 입력하세요."); }
        if (pin.Length != 32) throw new ArgumentException("SHA-256 지문은 64자리 16진수입니다.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null && DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() &&
            DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), pin);
        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
    }
    public void SetToken(string token) => _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    public async Task<T> Post<T>(string route, object? request = null, CancellationToken cancellationToken = default, int timeoutMs = 8000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var response = request is null ? await _http.PostAsync(route, null, timeout.Token) :
            await _http.PostAsJsonAsync(route, request, JsonDefaults.Options, timeout.Token);
        return await Read<T>(response, timeout.Token);
    }
    public async Task<T> Get<T>(string route, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(8000);
        using var response = await _http.GetAsync(route, timeout.Token); return await Read<T>(response, timeout.Token);
    }
    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync<ApiError>(JsonDefaults.Options, ct); } catch (JsonException) { }
            throw new ApiException(error?.Code ?? "http_error", error?.Message ?? $"호스트 응답: {(int)response.StatusCode}", response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, ct) ?? throw new InvalidDataException("호스트 응답이 비어 있습니다.");
    }
    public async Task<MediaPayload> GetMedia(Guid camera, int version, string asset, CancellationToken cancellationToken)
    {
        if (!MediaLimits.ValidAsset(asset)) throw MediaLimits.Invalid();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(17000);
        using var response = await _http.GetAsync($"/api/cameras/{camera}/hls/{asset}?version={version}",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) _ = await Read<MediaPayload>(response, timeout.Token).ConfigureAwait(false);
        var limit = asset.EndsWith(".m3u8", StringComparison.Ordinal) ? MediaLimits.PlaylistBytes : MediaLimits.SegmentBytes;
        if (response.Content.Headers.ContentLength > limit) throw MediaLimits.Invalid();
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (memory.Length + count > limit) throw MediaLimits.Invalid();
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        if (asset.EndsWith(".m3u8", StringComparison.Ordinal)) MediaLimits.ValidatePlaylist(bytes);
        return new(bytes, asset.EndsWith(".m3u8", StringComparison.Ordinal) ? "application/vnd.apple.mpegurl" :
            asset.EndsWith(".ts", StringComparison.Ordinal) ? "video/mp2t" : "video/mp4");
    }
    public void Dispose() => _http.Dispose();
}
public sealed record ClientPreferences(Guid PcId, string Endpoint, string Fingerprint)
{
    public static string ProfilePath
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--profile-dir");
            var directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntegratedContro");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "client.json");
        }
    }
    public static ClientPreferences Load()
    {
        var path = ProfilePath;
        if (File.Exists(path))
            return JsonSerializer.Deserialize<ClientPreferences>(File.ReadAllText(path), JsonDefaults.Options)
                ?? throw new InvalidDataException("앱 설정 파일을 확인하세요.");
        var value = new ClientPreferences(Guid.NewGuid(), "", ""); value.Save(); return value;
    }
    public void Save() => File.WriteAllText(ProfilePath, JsonSerializer.Serialize(this, JsonDefaults.Options));
}
