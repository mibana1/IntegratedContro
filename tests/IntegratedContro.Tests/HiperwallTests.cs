using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class HiperwallTests
{
    private sealed class MemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<Guid, string> _values = [];
        public Guid Save(string value) { var id = Guid.NewGuid(); _values[id] = value; return id; }
        public string Read(Guid id) => _values[id];
    }

    private sealed class OrderedReader : IHiperwallReader
    {
        private int _count;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HiperwallReading> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HiperwallReading> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HiperwallReading> ReadAsync(HiperwallConfiguration c, string? s, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) == 1) { FirstStarted.SetResult(); return First.Task; }
            SecondStarted.SetResult(); return Second.Task;
        }
    }
    private sealed class DelayedReader : IHiperwallReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HiperwallReading> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HiperwallReading> ReadAsync(HiperwallConfiguration c, string? s, CancellationToken ct)
        { Started.TrySetResult(); return await Finish.Task; } // Deliberately ignores cancellation to test rejection at commit.
    }

    [Fact]
    public async Task Older_session_completion_cannot_overwrite_newer_query_status()
    {
        var reader = new OrderedReader(); using var rig = new Rig(reader, new MemoryCredentials());
        var viewer = rig.Operator("viewer", AccountRole.Viewer);
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000"));
        var old = rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        await reader.FirstStarted.Task;
        var current = rig.Service.RefreshHiperwallAsync(viewer.Token, false, default);
        await reader.SecondStarted.Task;
        reader.Second.SetResult(Success()); await current;
        reader.First.SetResult(Success() with { State = HiperwallConnectionState.ConnectionFailed });
        await old;
        Assert.Equal(HiperwallConnectionState.Connected, rig.Service.GetHiperwallStatus(viewer.Token).State);
    }
    private static HiperwallConfiguration Config(string endpoint) => new(1, "fixture", endpoint, HiperwallAuthentication.Token, "3", 3000, null);
    private static SaveHiperwallRequest Save(Rig rig, string endpoint, int version = 0, int timeout = 3000) =>
        new(rig.Generation, version, "가짜 서버 검증", endpoint, HiperwallAuthentication.Token, "3", timeout, FakeHiperwallServer.FixtureSecret);
    private static HiperwallReading Success() => new(HiperwallConnectionState.Connected, "fixture", null,
        HiperwallLabels.NotQueried(), HiperwallLabels.NotQueried(), new(HiperwallListState.Available, "fixture", []));
    [Fact]
    public async Task Read_only_protocol_preserves_unicode_duplicate_names_and_absent_ids()
    {
        await using var server = new FakeHiperwallServer();
        using var reader = new HiperwallHttpReader();
        var result = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(HiperwallConnectionState.Connected, result.State);
        Assert.Equal("2026 R2", result.Controller!.Version);
        Assert.Equal(new[] { "uuid-1", "uuid-2", null }, result.Contents.Items.Select(x => x.Id));
        Assert.Equal(result.Contents.Items[0].Name, result.Contents.Items[1].Name);
        Assert.Equal(new[] { "zone-1", "zone-2" }, result.Zones.Items.Select(x => x.Id));
        Assert.Equal("-1920.5", result.Zones.Items[0].Fields["left"]);
        Assert.Equal(HiperwallListState.Unsupported, result.Walls.State);
        Assert.Equal(new[] { "GET /hello", "list", "walls", "list/open" }, server.Operations.ToArray());
    }
    [Theory]
    [InlineData("<Objects/>", HiperwallListState.Available)]
    [InlineData("<html>login page</html>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object/></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object><name>A</name>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object><name>A</name><uuid>same</uuid></Object><Object><name>B</name><uuid>same</uuid></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<!DOCTYPE Objects [<!ENTITY leak SYSTEM 'http://127.0.0.1:1/private'>]><Objects><Object><name>&leak;</name></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Error><code>403</code><token>DO-NOT-EXPOSE</token></Error></Objects>", HiperwallListState.Failed)]
    public async Task Empty_invalid_xml_and_embedded_errors_are_distinguished(string xml, HiperwallListState expected)
    {
        await using var server = new FakeHiperwallServer { Contents = xml };
        using var reader = new HiperwallHttpReader();
        var result = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(expected, result.Contents.State);
        Assert.Empty(result.Contents.Items);
        Assert.DoesNotContain("DO-NOT-EXPOSE", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("<Walls/>", HiperwallListState.Available, HiperwallListState.Unsupported)]
    [InlineData("<Walls><Wall><invented>no</invented></Wall></Walls>", HiperwallListState.Unsupported, HiperwallListState.Unsupported)]
    [InlineData("<Zones/>", HiperwallListState.Unsupported, HiperwallListState.Available)]
    [InlineData("<Zones><Zone><id>x</id><top>NaN</top></Zone></Zones>", HiperwallListState.Unsupported, HiperwallListState.Unsupported)]
    public async Task Wall_zone_modes_never_synthesize_inventory(string xml, HiperwallListState walls, HiperwallListState zones)
    {
        await using var server = new FakeHiperwallServer { Walls = xml };
        using var reader = new HiperwallHttpReader();
        var result = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(walls, result.Walls.State); Assert.Equal(zones, result.Zones.State);
        Assert.Empty(result.Walls.Items); Assert.Empty(result.Zones.Items);
    }
    [Theory]
    [InlineData(401, HiperwallConnectionState.AuthenticationFailed)]
    [InlineData(403, HiperwallConnectionState.AuthenticationFailed)]
    [InlineData(404, HiperwallConnectionState.Unsupported)]
    [InlineData(405, HiperwallConnectionState.Unsupported)]
    [InlineData(408, HiperwallConnectionState.TimedOut)]
    [InlineData(500, HiperwallConnectionState.ConnectionFailed)]
    [InlineData(302, HiperwallConnectionState.ConnectionFailed)]
    public async Task Http_failures_never_expose_response_body(int status, HiperwallConnectionState expected)
    {
        await using var server = new FakeHiperwallServer { Handler = _ => Task.FromResult(new FakeHiperwallServer.Response("SECRET-ERROR-RESPONSE", status)) };
        using var reader = new HiperwallHttpReader();
        var result = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(expected, result.State); Assert.DoesNotContain("SECRET-ERROR-RESPONSE", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("Hiperwall,2026 R2,Crypto:30,Primary", HiperwallConnectionState.Unsupported)]
    [InlineData("Hiperwall,2026 R2,None,Primary", HiperwallConnectionState.AuthenticationFailed)]
    [InlineData("Not a Hiperwall hello", HiperwallConnectionState.Unsupported)]
    public async Task Hello_compatibility_does_not_guess_auth_or_version(string hello, HiperwallConnectionState expected)
    {
        await using var server = new FakeHiperwallServer { Hello = hello };
        using var reader = new HiperwallHttpReader();
        var result = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(expected, result.State); Assert.Single(server.Operations);
    }
    [Fact]
    public async Task Settings_authorization_validation_secrets_and_restart()
    {
        using var reader = new HiperwallHttpReader(); var secrets = new MemoryCredentials();
        using var rig = new Rig(reader, secrets);
        Assert.Equal(HiperwallConnectionState.NotConfigured, rig.Service.GetHiperwallStatus(rig.Admin.Token).State);
        var viewer = rig.Operator("viewer", AccountRole.Viewer);
        Rig.Reject("admin_required", () => rig.Service.GetHiperwallSettings(viewer.Token));
        Rig.Reject("unauthorized", () => rig.Service.GetHiperwallStatus(""));
        Rig.Reject("lease_required", () => rig.Service.SaveHiperwallSettings(viewer.Token, Save(rig, "http://localhost:8000")));
        foreach (var endpoint in new[] { "http://localhost", "http://user:pass@localhost:8000", "http://localhost:8000/path", "ftp://localhost:8000", "http://localhost:8000/?token=x" })
            Rig.Reject("invalid_endpoint", () => rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, endpoint)));
        Rig.Reject("invalid_timeout", () => rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000", timeout: 0)));
        Rig.Reject("secret_required", () => rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000") with { NewSecret = null }));
        var settings = rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000"));
        Assert.True(settings.HasSecret);
        var ref1 = rig.Store.Load().Hiperwall!.CredentialId;
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000", 1) with { NewSecret = null });
        Assert.Equal(ref1, rig.Store.Load().Hiperwall!.CredentialId);
        Rig.Reject("secret_required", () => rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8001", 2) with { NewSecret = null }));
        Assert.DoesNotContain(FakeHiperwallServer.FixtureSecret, JsonSerializer.Serialize(rig.Store.Load()));
        Assert.DoesNotContain("credentialId", JsonSerializer.Serialize(settings, JsonDefaults.Options));
        rig.Service.Release(rig.Admin.Token, rig.Generation);
        Rig.Reject("lease_required", () => rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000", 2)));
        rig.Restart();
        Assert.Equal(2, rig.Service.GetHiperwallSettings(rig.Admin.Token).Version);
        Assert.Equal(HiperwallConnectionState.NotChecked, rig.Service.GetHiperwallStatus(rig.Admin.Token).State);
        Assert.Equal(FakeHiperwallServer.FixtureSecret, secrets.Read(ref1!.Value));
        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<DomainException>(() => rig.Service.RefreshHiperwallAsync(viewer.Token, false, default))).Code);
    }
    [Fact]
    public async Task Timeout_disconnect_clear_lists_and_query_does_not_flood_audit()
    {
        await using var server = new FakeHiperwallServer(); using var reader = new HiperwallHttpReader();
        using var rig = new Rig(reader, new MemoryCredentials());
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, server.Endpoint, timeout: 300));
        var first = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, true, default);
        Assert.Equal(HiperwallConnectionState.Connected, first.State);
        var count = rig.Store.Load().Audit.Count;
        var latest = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        Assert.Equal(count, rig.Store.Load().Audit.Count);
        server.Handler = _ => Task.FromResult(new FakeHiperwallServer.Response("delayed", DelayMs: 800));
        var timeout = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, true, default);
        Assert.Equal(HiperwallConnectionState.TimedOut, timeout.State); Assert.Empty(timeout.Contents.Items);
        Assert.Equal(first.LastSuccessAt, timeout.LastSuccessAt);
        Assert.Equal(latest.Contents.SucceededAt, timeout.Contents.SucceededAt);
        Assert.Equal(HiperwallListState.Failed, timeout.Contents.State);
        server.Handler = _ => Task.FromResult(new FakeHiperwallServer.Response("", Disconnect: true));
        var disconnected = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        Assert.Equal(HiperwallConnectionState.ConnectionFailed, disconnected.State); Assert.Empty(disconnected.Zones.Items);
    }
    [Fact]
    public async Task Late_response_after_settings_change_and_duplicate_refresh_are_rejected()
    {
        var reader = new DelayedReader(); using var rig = new Rig(reader, new MemoryCredentials());
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000"));
        var pending = rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        await reader.Started.Task;
        var busy = await Assert.ThrowsAsync<DomainException>(() => rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default));
        Assert.Equal("hiperwall_busy", busy.Code);
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8001", 1));
        reader.Finish.SetResult(Success());
        Assert.Equal("hiperwall_settings_changed", (await Assert.ThrowsAsync<DomainException>(() => pending)).Code);
        var current = rig.Service.GetHiperwallStatus(rig.Admin.Token);
        Assert.Equal(2, current.ConfigurationVersion); Assert.Empty(current.Contents.Items); Assert.Null(current.LastSuccessAt);
    }
    [Fact]
    public async Task Logout_revokes_inflight_and_future_reads()
    {
        var reader = new DelayedReader(); using var rig = new Rig(reader, new MemoryCredentials());
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000"));
        var viewer = rig.Operator("viewer", AccountRole.Viewer);
        var pending = rig.Service.RefreshHiperwallAsync(viewer.Token, false, default); await reader.Started.Task;
        rig.Service.Logout(viewer.Token); reader.Finish.SetResult(Success());
        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<DomainException>(() => pending)).Code);
        Rig.Reject("unauthorized", () => rig.Service.GetHiperwallStatus(viewer.Token));
        var fresh = rig.Login("viewer");
        Assert.Equal(HiperwallConnectionState.Connected, (await rig.Service.RefreshHiperwallAsync(fresh.Token, false, default)).State);
    }

    [Fact]
    public async Task None_authentication_and_bounded_payloads()
    {
        await using var server = new FakeHiperwallServer { Hello = "Hiperwall,9.0.0,None,Default", Contents = "<Objects/>" };
        using var reader = new HiperwallHttpReader();
        var config = Config(server.Endpoint) with { Authentication = HiperwallAuthentication.None };
        Assert.Equal(HiperwallConnectionState.Connected, (await reader.ReadAsync(config, null, default)).State);
        server.Contents = new string('x', 4 * 1024 * 1024 + 1);
        Assert.Equal(HiperwallConnectionState.Unsupported, (await reader.ReadAsync(config, null, default)).State);
        server.Contents = string.Concat(Enumerable.Repeat("<a>", 20)) + string.Concat(Enumerable.Repeat("</a>", 20));
        Assert.Equal(HiperwallConnectionState.Unsupported, (await reader.ReadAsync(config, null, default)).State);
    }
    [Fact]
    public async Task Cancellation_and_disabled_account_do_not_publish_late_inventory()
    {
        var reader = new DelayedReader(); using var rig = new Rig(reader, new MemoryCredentials());
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, "http://localhost:8000"));
        var viewer = rig.Operator("viewer", AccountRole.Viewer);
        var pending = rig.Service.RefreshHiperwallAsync(viewer.Token, false, default); await reader.Started.Task;
        rig.Service.UpdateAccount(rig.Admin.Token, new(rig.Generation, viewer.Session.UserId, false, true, []));
        reader.Finish.SetResult(Success());
        Assert.Equal("account_disabled", (await Assert.ThrowsAsync<DomainException>(() => pending)).Code);
        Assert.Empty(rig.Service.GetHiperwallStatus(rig.Admin.Token).Contents.Items);
    }
    [Fact]
    public async Task Explicit_cancellation_clears_results_without_audit_noise()
    {
        await using var server = new FakeHiperwallServer();
        using var reader = new HiperwallHttpReader(); using var rig = new Rig(reader, new MemoryCredentials());
        rig.Service.SaveHiperwallSettings(rig.Admin.Token, Save(rig, server.Endpoint));
        await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        var count = rig.Store.Load().Audit.Count;
        server.Handler = _ => Task.FromResult(new FakeHiperwallServer.Response("", DelayMs: 1000));
        using var cancel = new CancellationTokenSource(100);
        var result = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, cancel.Token);
        Assert.Equal(HiperwallListState.Failed, result.Contents.State);
        Assert.Empty(result.Contents.Items); Assert.NotNull(result.Contents.SucceededAt);
        Assert.Equal(count, rig.Store.Load().Audit.Count);
    }
    [Fact]
    public void Protected_store_never_creates_an_alternate_directory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new Rig();
        var missing = Path.Combine(rig.DirectoryPath, "missing");
        ICredentialStore credentials = new HiperwallCredentialStore(missing, new WindowsCurrentUserSecretProtector());
        Rig.Reject("credential_store_unavailable", () => credentials.Save("fixture"));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Separate_https_host_protects_token_and_recovers_same_data()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new FakeHiperwallServer();
        await using var host = new HostProcess(); await host.Initialize();
        var (client, _) = await host.Login();
        var lease = await HostProcess.Post<Lease>(client, "/api/lease/acquire");
        var settings = await HostProcess.Post<HiperwallSettingsView>(client, "/api/hiperwall/settings",
            new SaveHiperwallRequest(lease.Generation, 0, "가짜 서버 검증", server.Endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        Assert.True(settings.HasSecret);
        var result = await HostProcess.Post<HiperwallView>(client, "/api/hiperwall/test");
        Assert.Equal(HiperwallConnectionState.Connected, result.State);
        var exposed = await client.GetStringAsync("/api/state") + await client.GetStringAsync("/api/hiperwall/settings") + await client.GetStringAsync("/api/hiperwall/status");
        Assert.DoesNotContain(FakeHiperwallServer.FixtureSecret, exposed);
        Assert.DoesNotContain(JsonEncodedText.Encode(FakeHiperwallServer.FixtureSecret).ToString(), exposed);
        Assert.DoesNotContain("credentialId", exposed);
        Assert.Contains("Hiperwall 연결 테스트", (await HostProcess.State(client)).Audit.Last().EventName);
        var blob = Directory.GetFiles(host.DataPath, "hiperwall-*.dpapi").Single();
        Assert.DoesNotContain(FakeHiperwallServer.FixtureSecret, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(blob)));
        await HostProcess.Post<Lease>(client, "/api/lease/release", new LeaseRequest(lease.Generation));
        await host.Kill(); await host.Run(); // Only the test-owned isolated process is stopped.
        var (fresh, _) = await host.Login();
        Assert.Equal(HiperwallConnectionState.Connected, (await HostProcess.Post<HiperwallView>(fresh, "/api/hiperwall/refresh")).State);
        using var store = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(host.DataPath, "control.sqlite")};Mode=ReadOnly;Pooling=False");
        store.Open(); using var query = store.CreateCommand(); query.CommandText = "SELECT payload FROM host_state";
        Assert.DoesNotContain(JsonEncodedText.Encode(FakeHiperwallServer.FixtureSecret).ToString(), (string)query.ExecuteScalar()!);
        File.WriteAllBytes(blob, [1, 2, 3]); // Corrupt only this fixture secret, then verify explicit failure without fallback.
        var failed = await HostProcess.Post<HiperwallView>(fresh, "/api/hiperwall/test");
        Assert.Equal(HiperwallConnectionState.ConnectionFailed, failed.State);
        Assert.Contains("토큰", failed.Message); Assert.Empty(failed.Contents.Items);
    }
}
