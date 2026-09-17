using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class CameraTests
{
    private sealed class Secrets : IMediaSecretStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public Guid Save(string value) { var id = Guid.NewGuid(); Values.Add(id, value); return id; }
        public string Read(Guid id) => Values[id];
        public void Delete(Guid id) => Values.Remove(id);
    }
    private sealed class Media : IMediaMtxClient
    {
        public List<string> Calls { get; } = [];
        public bool Fail { get; set; }
        public Func<Task>? Gate { get; set; }
        public string? LastSource { get; private set; }
        public async Task EnsurePathAsync(MediaConfiguration c, MediaCredentials credentials, string path, string source, CancellationToken ct)
        { Calls.Add("ensure:" + path); LastSource = source; if (Gate is not null) await Gate(); if (Fail) throw new HttpRequestException("fake failure"); }
        public async Task DeletePathAsync(MediaConfiguration c, MediaCredentials credentials, string path, CancellationToken ct)
        { Calls.Add("delete:" + path); if (Gate is not null) await Gate(); if (Fail) throw new HttpRequestException("fake failure"); }
        public async Task<CameraConnection> GetStatusAsync(MediaConfiguration c, MediaCredentials credentials, string path, CancellationToken ct)
        { if (Gate is not null) await Gate(); return new(false, "입력 대기"); }
        public async Task<MediaPayload> ReadHlsAsync(MediaConfiguration c, MediaCredentials credentials, string path, string asset, CancellationToken ct)
        { if (Gate is not null) await Gate(); return new("#EXTM3U\n#EXTINF:2,\ns1.ts\n"u8.ToArray(), "application/vnd.apple.mpegurl"); }
    }
    private static SaveMediaSettingsRequest Settings(Rig r, int version = 0) =>
        new(r.Generation, version, "http://localhost:9997", "http://localhost:8888", "api", "reader", "api-test-password", "hls-test-password");
    private static CameraView Save(Rig r, Guid? id = null, int version = 0, bool enabled = true) =>
        r.Service.SaveCamera(r.Admin.Token, new(r.Generation, id ?? Guid.NewGuid(), version, "카메라 A", "관제실", enabled,
            "rtsp://private-camera:8554/input?private-key=value", "camera-user", "camera-password"));
    private static CameraActionRequest Action(Rig r, CameraView c, bool force = false) => new(r.Generation, c.Id, c.Version, force);
    private static Rig Setup(Media media, Secrets secrets)
    {
        var rig = new Rig(media: media, mediaSecrets: secrets);
        rig.Service.SaveMediaSettings(rig.Admin.Token, Settings(rig)); return rig;
    }
    [Fact]
    public async Task Registration_is_durable_before_provisioning_and_secrets_never_enter_public_or_database_state()
    {
        var media = new Media { Fail = true }; var secrets = new Secrets(); using var r = Setup(media, secrets);
        var camera = Save(r);
        Assert.Empty(media.Calls); Assert.Equal(CameraProvisioning.Pending, camera.Provisioning);
        await r.Service.ReconcileCamerasAsync();
        Assert.Equal(CameraProvisioning.Failed, r.Service.GetCameras(r.Admin.Token).Cameras.Single().Provisioning);
        foreach (var value in new[] { JsonSerializer.Serialize(r.Store.Load()), JsonSerializer.Serialize(r.Service.GetCameras(r.Admin.Token)) })
        {
            Assert.DoesNotContain("private-camera", value); Assert.DoesNotContain("camera-password", value);
            Assert.DoesNotContain("api-test-password", value); Assert.DoesNotContain("hls-test-password", value);
        }
        Assert.Null(r.Store.Load().Hiperwall);
        media.Fail = false; r.Service.SyncCamera(r.Admin.Token, Action(r, camera)); await r.Service.ReconcileCamerasAsync();
        Assert.Equal(CameraProvisioning.Ready, r.Service.GetCameras(r.Admin.Token).Cameras.Single().Provisioning);
        Assert.False((await r.Service.GetCameraStatusAsync(r.Admin.Token, camera.Id, camera.Version, default)).Ready);
    }
    [Fact]
    public async Task Normal_delete_failure_retains_registration_until_explicit_retry()
    {
        var media = new Media(); using var r = Setup(media, new()); var c = Save(r); await r.Service.ReconcileCamerasAsync();
        media.Fail = true; r.Service.DeleteCamera(r.Admin.Token, Action(r, c));
        await r.Service.ReconcileCamerasAsync();
        var failed = r.Service.GetCameras(r.Admin.Token).Cameras.Single();
        Assert.Equal(CameraProvisioning.DeleteFailed, failed.Provisioning); Assert.Empty(r.Store.Load().CameraCleanup);
        media.Fail = false; await r.Service.ReconcileCamerasAsync(); Assert.Single(r.Store.Load().Cameras);
        r.Service.DeleteCamera(r.Admin.Token, Action(r, failed)); await r.Service.ReconcileCamerasAsync();
        Assert.Empty(r.Store.Load().Cameras);
    }
    [Fact]
    public async Task Restart_preserves_failed_normal_delete_without_recreating_the_path()
    {
        var media = new Media(); using var r = Setup(media, new()); var c = Save(r); await r.Service.ReconcileCamerasAsync();
        media.Fail = true; r.Service.DeleteCamera(r.Admin.Token, Action(r, c)); await r.Service.ReconcileCamerasAsync();
        var count = media.Calls.Count;
        r.Restart(); media.Fail = false; await r.Service.ReconcileCamerasAsync();
        Assert.Equal(count, media.Calls.Count);
        Assert.Equal(CameraProvisioning.DeleteFailed, r.Store.Load().Cameras.Single().Provisioning);
    }
    [Fact]
    public async Task Force_delete_racing_late_sync_never_resurrects_and_cleans_exact_old_path_after_release()
    {
        var media = new Media(); var secrets = new Secrets(); using var r = Setup(media, secrets); var camera = Save(r);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.Gate = () => { started.TrySetResult(); return finish.Task; };
        var running = r.Service.ReconcileCamerasAsync(); await started.Task;
        r.Service.DeleteCamera(r.Admin.Token, Action(r, camera, true));
        var second = Save(r, camera.Id); // Same local ID, immutable new path: old cleanup must never delete it.
        Assert.NotEqual(camera.StreamPath, second.StreamPath);
        r.Service.Release(r.Admin.Token, r.Generation);
        finish.SetResult(); await running; media.Gate = null;
        await r.Service.ReconcileCamerasAsync(); await r.Service.ReconcileCamerasAsync();
        Assert.Equal(second.StreamPath, r.Store.Load().Cameras.Single().StreamPath);
        Assert.Contains("delete:" + camera.StreamPath, media.Calls); Assert.DoesNotContain("delete:" + second.StreamPath, media.Calls);
        Assert.Empty(r.Store.Load().CameraCleanup); Assert.Equal(2, secrets.Values.Count);
    }
    [Fact]
    public async Task Disable_reenable_and_restart_reconcile_existing_paths_without_changing_identity()
    {
        var media = new Media(); using var r = Setup(media, new()); var c = Save(r); await r.Service.ReconcileCamerasAsync();
        var disabled = Save(r, c.Id, c.Version, false); await r.Service.ReconcileCamerasAsync();
        Assert.Equal(CameraProvisioning.Disabled, r.Store.Load().Cameras.Single().Provisioning);
        Assert.Contains("delete:" + c.StreamPath, media.Calls);
        var enabled = Save(r, c.Id, disabled.Version); await r.Service.ReconcileCamerasAsync();
        r.Restart(); Assert.Equal(CameraProvisioning.Pending, r.Store.Load().Cameras.Single().Provisioning);
        await r.Service.ReconcileCamerasAsync();
        Assert.Equal(enabled.StreamPath, r.Store.Load().Cameras.Single().StreamPath);
        Assert.Equal(CameraProvisioning.Ready, r.Store.Load().Cameras.Single().Provisioning);
    }
    [Fact]
    public async Task Failed_force_cleanup_survives_restart_and_honors_original_endpoint()
    {
        var media = new Media { Fail = true }; using var r = Setup(media, new()); var c = Save(r);
        r.Service.DeleteCamera(r.Admin.Token, Action(r, c, true)); await r.Service.ReconcileCamerasAsync();
        Assert.Single(r.Store.Load().CameraCleanup);
        Rig.Reject("media_in_use", () => r.Service.SaveMediaSettings(r.Admin.Token, Settings(r, 1) with { ApiEndpoint = "http://other:9997" }));
        r.Restart(); media.Fail = false; // Restart retries immediately, before the 30-second due time.
        await r.Service.ReconcileCamerasAsync(); Assert.Empty(r.Store.Load().CameraCleanup);
        Assert.All(media.Calls, call => Assert.Equal("delete:" + c.StreamPath, call));
    }
    [Fact]
    public async Task Permission_version_and_disabled_playback_are_enforced()
    {
        var media = new Media(); using var r = Setup(media, new()); var c = Save(r);
        var viewer = r.Operator("viewer", AccountRole.Viewer);
        Assert.Single(r.Service.GetCameras(viewer.Token).Cameras);
        Rig.Reject("version_conflict", () => Save(r, c.Id, 0));
        Rig.Reject("lease_required", () => r.Service.DeleteCamera(viewer.Token, Action(r, c)));
        var op = r.Operator("operator"); r.Service.Release(r.Admin.Token, r.Generation); var gen = r.Service.Acquire(op.Token).Generation;
        Rig.Reject("admin_required", () => r.Service.DeleteCamera(op.Token, new(gen, c.Id, c.Version)));
        r.Service.Release(op.Token, gen); r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        Save(r, c.Id, c.Version, false); await r.Service.ReconcileCamerasAsync();
        await Assert.ThrowsAsync<DomainException>(() => r.Service.ReadCameraHlsAsync(viewer.Token, c.Id, c.Version + 1, "index.m3u8", default));
        Assert.Single(media.Calls); // Disabled only: delete; no phantom stream read/write.
    }
    [Fact]
    public async Task Late_Hls_read_is_discarded_after_delete_or_logout()
    {
        var media = new Media(); using var r = Setup(media, new()); var c = Save(r); await r.Service.ReconcileCamerasAsync();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.Gate = () => finish.Task;
        var reading = r.Service.ReadCameraHlsAsync(r.Admin.Token, c.Id, c.Version, "index.m3u8", default);
        r.Service.Logout(r.Admin.Token); finish.SetResult();
        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<DomainException>(() => reading)).Code);
    }
    [Fact]
    public async Task Source_edit_during_provisioning_keeps_latest_intent_and_collects_old_secret_after_io()
    {
        var media = new Media(); var secrets = new Secrets(); using var r = Setup(media, secrets); var c = Save(r);
        var oldRef = r.Store.Load().Cameras.Single().SourceCredentialId;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.Gate = () => finish.Task; var running = r.Service.ReconcileCamerasAsync();
        var next = Save(r, c.Id, c.Version);
        Assert.True(secrets.Values.ContainsKey(oldRef));
        finish.SetResult(); await running; media.Gate = null;
        Assert.Equal(CameraProvisioning.Pending, r.Store.Load().Cameras.Single().Provisioning);
        await r.Service.ReconcileCamerasAsync(); Assert.Equal(next.Version, r.Store.Load().Cameras.Single().Version);
        Assert.False(secrets.Values.ContainsKey(oldRef));
    }
    [Theory]
    [InlineData("http://camera/stream")]
    [InlineData("rtsp:///")]
    [InlineData("rtsp://camera/a#fragment")]
    [InlineData("rtsp://camera/\nsecret")]
    public void Invalid_camera_sources_are_rejected_without_saving(string source)
    {
        var media = new Media(); var secrets = new Secrets(); using var r = Setup(media, secrets);
        Assert.Throws<DomainException>(() => r.Service.SaveCamera(r.Admin.Token,
            new(r.Generation, Guid.NewGuid(), 0, "camera", "", true, source)));
        Assert.Empty(r.Store.Load().Cameras); Assert.Single(secrets.Values);
    }
    [Fact]
    public void Protected_media_store_round_trip_and_delete_do_not_leave_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "IntegratedControTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new MediaCredentialStore(dir, new WindowsCurrentUserSecretProtector()); var id = store.Save("rtsp://protected:test@camera/feed");
            Assert.Equal("rtsp://protected:test@camera/feed", store.Read(id));
            Assert.DoesNotContain("rtsp://", Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(dir).Single())));
            store.Delete(id); Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    [Fact]
    public async Task MediaMtx_adapter_uses_v3_routes_separate_auth_and_exact_source_without_success_from_error_body()
    {
        var calls = new List<string>(); var configured = false;
        using var handler = new Handler(async request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            var auth = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!));
            Assert.Equal(request.RequestUri.Port == 8888 ? "reader:hls-pass" : "api:api-pass", auth);
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("rtsp://user:p%40ss@camera/stream", doc.RootElement.GetProperty("source").GetString());
                Assert.Equal("tcp", doc.RootElement.GetProperty("rtspTransport").GetString()); configured = true;
            }
            if (request.RequestUri.AbsolutePath.StartsWith("/v3/config/paths/get", StringComparison.Ordinal))
                return new(configured ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new StringContent("{\"source\":\"rtsp://user:p%40ss@camera/stream\",\"sourceOnDemand\":false,\"rtspTransport\":\"tcp\"}") };
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Port == 8888 ? "#EXTM3U\n#EXTINF:2,\ns1.ts\n" : "{\"ready\":true}") };
        });
        using var client = new MediaMtxHttpClient(handler);
        var config = new MediaConfiguration(1, "http://localhost:9997", "http://localhost:8888", "api", "reader", Guid.NewGuid());
        var credentials = new MediaCredentials("api-pass", "hls-pass");
        await client.EnsurePathAsync(config, credentials, "ic-test", "rtsp://user:p%40ss@camera/stream", default);
        await client.EnsurePathAsync(config, credentials, "ic-test", "rtsp://user:p%40ss@camera/stream", default);
        Assert.True((await client.GetStatusAsync(config, credentials, "ic-test", default)).Ready);
        await client.ReadHlsAsync(config, credentials, "ic-test", "index.m3u8", default);
        Assert.Equal(1, calls.Count(c => c.StartsWith("POST", StringComparison.Ordinal)));
        Assert.Contains("GET /ic-test/index.m3u8", calls);
    }
    [Theory]
    [InlineData("#EXTM3U\nhttps://external/segment.ts")]
    [InlineData("#EXTM3U\n../another/s1.ts")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.ts\"")]
    [InlineData("#EXTM3U\n#EXT-X-MAP:URI=\"http://external/init.mp4\"")]
    [InlineData("<html>login required</html>")]
    public void Playlist_references_cannot_escape_registered_camera(string value) =>
        Assert.Throws<DomainException>(() => MediaLimits.ValidatePlaylist(Encoding.UTF8.GetBytes(value)));
    [Fact]
    public void Compressed_and_decoded_preview_limits_reject_bombs()
    {
        Assert.Throws<DomainException>(() => PreviewImageLimits.Dimensions(new byte[MediaLimits.ImageBytes + 1]));
        var png = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 65536);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 65536);
        Assert.Throws<DomainException>(() => PreviewImageLimits.Dimensions(png));
        MediaLimits.ValidatePlaylist("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:2,\ns1.m4s\n"u8.ToArray());
    }
    [Fact]
    public async Task Hls_session_cookie_stays_at_host_and_redirects_cannot_change_origin_or_camera()
    {
        var calls = new List<Uri>(); var session = Guid.NewGuid().ToString("D");
        using var client = new MediaMtxHttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            if (request.RequestUri!.AbsolutePath.EndsWith("/index.m3u8", StringComparison.Ordinal))
            {
                if (request.RequestUri.Query == "")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("/ic-test/index.m3u8?cookieCheck=1", UriKind.Relative); return Task.FromResult(redirect);
                }
                Assert.Equal("cookieCheck=1", request.Headers.GetValues("Cookie").Single());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\nmain.m3u8\n") };
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=" + session + "; HttpOnly; Secure");
                return Task.FromResult(response);
            }
            Assert.Equal("session=" + session, request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:2,\ns1.ts\n") });
        }));
        var config = new MediaConfiguration(1, "http://localhost:9997", "http://localhost:8888", "api", "reader", Guid.NewGuid());
        var credentials = new MediaCredentials("api-pass", "reader-pass");
        var initial = await client.ReadHlsAsync(config, credentials, "ic-test", "index.m3u8", default);
        await client.ReadHlsAsync(config, credentials, "ic-test", "main.m3u8", default);
        Assert.DoesNotContain(session, Encoding.UTF8.GetString(initial.Bytes));
        Assert.All(calls, uri => Assert.DoesNotContain(session, uri.AbsoluteUri));
        var redirected = 0;
        using var hostile = new MediaMtxHttpClient(new Handler(request =>
        {
            redirected++; var result = new HttpResponseMessage(HttpStatusCode.Found);
            result.Headers.Location = new Uri("http://external/index.m3u8?cookieCheck=1"); return Task.FromResult(result);
        }));
        await Assert.ThrowsAsync<DomainException>(() => hostile.ReadHlsAsync(config, credentials, "ic-test", "index.m3u8", default));
        Assert.Equal(1, redirected);
    }
    [Fact]
    public async Task Hls_streaming_read_limit_checks_content_length_before_allocating()
    {
        using var client = new MediaMtxHttpClient(new Handler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentLength = MediaLimits.SegmentBytes + 1; return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<DomainException>(() => client.ReadHlsAsync(
            new(1, "http://localhost:9997", "http://localhost:8888", "api", "reader", Guid.NewGuid()),
            new("api-pass", "hls-pass"), "ic-test", "s1.ts", default));
    }

    [Fact]
    public async Task Preview_uses_inventory_allowlist_and_binary_validation()
    {
        await using var server = new FakeHiperwallServer();
        using var reader = new HiperwallHttpReader(); using var r = new Rig(reader, new Secrets());
        r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 0, "fixture", server.Endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        var error = await Assert.ThrowsAsync<DomainException>(() => r.Service.ReadHiperwallPreviewAsync(r.Admin.Token, new(1, "uuid", "arbitrary"), default));
        Assert.Equal("preview_not_found", error.Code);
        server.Handler = request => Task.FromResult(new FakeHiperwallServer.Response("<Error>private</Error>") { ContentType = "image/png" });
        await Assert.ThrowsAsync<DomainException>(() => r.Service.ReadHiperwallPreviewAsync(r.Admin.Token, new(1, "uuid", "uuid-1"), default));
    }
}
