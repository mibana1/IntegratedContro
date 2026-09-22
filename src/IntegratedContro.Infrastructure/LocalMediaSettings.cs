using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

// Only the host's generated local configuration is managed. Unknown edits are preserved.
public sealed class LocalMediaSettings(string dataPath) : ILocalMediaSettings
{
    private string Folder => Path.Combine(dataPath, "MediaMTX");
    private string Manifest => Path.Combine(Folder, "setup.json");
    private string Configuration => Path.Combine(Folder, "mediamtx.yml");
    public bool IsManaged
    {
        get
        {
            try { _ = File.GetAttributes(Folder); return true; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
            catch (Exception e) when (FileFailure(e)) { throw Failure(); }
        }
    }
    public int Validate(MediaConfiguration configuration, MediaCredentials credentials)
    {
        try
        {
            var record = ReadManifest();
            if (record.Configuration != configuration || !Equal(ReadText(Configuration), ManagedMediaConfiguration.ConfigurationText(record, credentials)))
                throw Changed();
            return record.RtspPort;
        }
        catch (Exception e) when (FileFailure(e)) { throw Failure(); }
    }
    public void Apply(LocalMediaChange change, MediaCredentials before, MediaCredentials after)
    {
        try
        {
            var restoring = change.Phase is LocalMediaChangePhase.Restoring or LocalMediaChangePhase.AwaitingRestoreVerification;
            var original = Record(change.Before, change.RtspPort);
            var proposed = Record(change.After, change.RtspPort);
            var restored = original with { ConfigurationVersion = change.After.Version + 1 };
            var manifest = ReadManifest();
            if (manifest != original && manifest != proposed && manifest != restored) throw Changed();
            var oldText = ManagedMediaConfiguration.ConfigurationText(original, before);
            var newText = ManagedMediaConfiguration.ConfigurationText(proposed, after);
            var current = ReadText(Configuration);
            if (!Equal(current, oldText) && !Equal(current, newText)) throw Changed();
            var desired = restoring ? oldText : newText;
            if (!Equal(current, desired)) Replace(Configuration, desired, current);
            var target = restoring ? restored : proposed;
            if (manifest != target)
            {
                var manifestText = ReadText(Manifest);
                if (JsonSerializer.Deserialize<ManagedMediaSetup>(manifestText, JsonDefaults.Options) != manifest) throw Changed();
                Replace(Manifest, JsonSerializer.Serialize(target, JsonDefaults.Options), manifestText);
            }
        }
        catch (Exception e) when (FileFailure(e)) { throw Failure(); }
    }
    private static ManagedMediaSetup Record(MediaConfiguration config, int rtsp) =>
        new(1, new Uri(config.ApiEndpoint).Port, new Uri(config.HlsEndpoint).Port, rtsp, config.CredentialId) { ConfigurationVersion = config.Version };
    private ManagedMediaSetup ReadManifest()
    {
        var value = JsonSerializer.Deserialize<ManagedMediaSetup>(ReadText(Manifest), JsonDefaults.Options);
        if (value is null || value.Version != 1 || value.ConfigurationVersion < 1 || value.CredentialId == Guid.Empty ||
            new[] { value.ApiPort, value.HlsPort, value.RtspPort }.Any(p => p is < 1024 or > 65535) ||
            new[] { value.ApiPort, value.HlsPort, value.RtspPort }.Distinct().Count() != 3) throw Changed();
        return value;
    }
    private string ReadText(string path)
    {
        _ = LocalHostDataPath.Validate(Folder, false);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw Changed();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 16384) throw Changed();
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    private void Replace(string path, string value, string expected)
    {
        if (ReadText(path) != expected) throw Changed();
        var temporary = Path.Combine(Folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true); }
            File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool Equal(string first, string second) => first.Replace("\r\n", "\n") == second.Replace("\r\n", "\n");
    private static bool FileFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
    private static DomainException Changed() => new("local_media_changed",
        "자동 생성한 로컬 영상 서버 파일과 접속 정보가 다릅니다. 외부 수정·누락 파일을 확인하세요. 기존 파일을 덮어쓰지 않습니다.", 409);
    private static DomainException Failure() => new("local_media_files",
        "로컬 영상 서버 파일을 읽거나 저장하지 못했습니다. 호스트의 데이터 폴더 권한·잠금·파일 상태를 확인하세요.", 503);

    public async Task VerifyAsync(MediaConfiguration config, MediaCredentials credentials, MediaCredentials rejectedCredentials,
        Guid changeId, CancellationToken ct)
    {
        // Validate guarantees these are the generated loopback endpoints, never a client-supplied address.
        Validate(config, credentials);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? user, string? password, object? body = null, CancellationToken? token = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (user is not null) request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
            if (body is not null) request.Content = JsonContent.Create(body);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token ?? ct);
        }
        static void Need(bool ok) { if (!ok) throw new DomainException("local_media_auth", "영상 서버 인증 적용을 확인하지 못했습니다.", 502); }
        var api = config.ApiEndpoint + "/v3";
        using (var ready = await Send(HttpMethod.Get, api + "/paths/list", config.ApiUser, credentials.ApiPassword))
        {
            Need(ready.StatusCode == HttpStatusCode.OK);
            await ready.Content.LoadIntoBufferAsync(65536, ct);
            using var json = JsonDocument.Parse(await ready.Content.ReadAsByteArrayAsync(ct));
            Need(json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array);
        }
        using (var anonymous = await Send(HttpMethod.Get, api + "/paths/list", null, null)) Need(anonymous.StatusCode == HttpStatusCode.Unauthorized);
        using (var reader = await Send(HttpMethod.Get, api + "/paths/list", config.HlsUser, credentials.HlsPassword)) Need(reader.StatusCode == HttpStatusCode.Unauthorized);
        if (credentials.ApiPassword != rejectedCredentials.ApiPassword)
            using (var old = await Send(HttpMethod.Get, api + "/paths/list", config.ApiUser, rejectedCredentials.ApiPassword)) Need(old.StatusCode == HttpStatusCode.Unauthorized);
        var path = "ic-settings-" + changeId.ToString("N");
        // The durable change ID makes a lost verification response safe to retry and clean up.
        using (var stale = await Send(HttpMethod.Delete, api + "/config/paths/delete/" + path, config.ApiUser, credentials.ApiPassword))
            Need(stale.IsSuccessStatusCode || stale.StatusCode == HttpStatusCode.NotFound);
        try
        {
            using (var added = await Send(HttpMethod.Post, api + "/config/paths/add/" + path, config.ApiUser, credentials.ApiPassword, new { source = "publisher" }))
                Need(added.IsSuccessStatusCode);
            var hls = config.HlsEndpoint + "/" + path + "/";
            using (var response = await Send(HttpMethod.Get, hls, config.HlsUser, credentials.HlsPassword))
                Need(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/html");
            using (var anonymous = await Send(HttpMethod.Get, hls, null, null)) Need(anonymous.StatusCode == HttpStatusCode.Unauthorized);
            using (var apiUser = await Send(HttpMethod.Get, hls, config.ApiUser, credentials.ApiPassword)) Need(apiUser.StatusCode == HttpStatusCode.Unauthorized);
            if (credentials.HlsPassword != rejectedCredentials.HlsPassword)
                using (var old = await Send(HttpMethod.Get, hls, config.HlsUser, rejectedCredentials.HlsPassword)) Need(old.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var removed = await Send(HttpMethod.Delete, api + "/config/paths/delete/" + path, config.ApiUser, credentials.ApiPassword, token: cleanup.Token);
            Need(removed.IsSuccessStatusCode || removed.StatusCode == HttpStatusCode.NotFound);
        }
    }
}
