using System.Net.Http.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class HiperwallDisplayTests
{
    private sealed class Secrets : ICredentialStore
    {
        public Guid Save(string secret) => Guid.NewGuid();
        public string Read(Guid reference) => FakeHiperwallServer.FixtureSecret;
    }
    private sealed class Probe : IHiperwallReader, IHiperwallWriter, IDisposable
    {
        private readonly HiperwallHttpReader _inner = new();
        public bool LoseOpen, LoseClose, FailReads;
        public Action<HiperwallWireCommand>? BeforeWrite;
        public Func<Task>? BeforeRead;
        public async Task<HiperwallReading> ReadAsync(HiperwallConfiguration c, string? s, CancellationToken ct)
        {
            if (BeforeRead is { } action) await action();
            if (FailReads) throw new HttpRequestException("fixture offline");
            return await _inner.ReadAsync(c, s, ct);
        }
        public async Task<HiperwallWriteResult> WriteAsync(HiperwallConfiguration c, string? s, HiperwallWireCommand command, CancellationToken ct)
        {
            BeforeWrite?.Invoke(command);
            var result = await _inner.WriteAsync(c, s, command, ct);
            return command.Action == HiperwallEditAction.Open && LoseOpen || command.Action == HiperwallEditAction.Close && LoseClose
                ? new(HiperwallSendState.Unknown, "fixture lost reply") : result;
        }
        public void Dispose() => _inner.Dispose();
    }
    private static HiperwallPlacement Placement => new("uuid", "source-1", "zone-1", new(-10.5, 0, 640.25, 360.5), 37, true);
    private static void Configure(Rig r, string endpoint) => r.Service.SaveHiperwallSettings(r.Admin.Token,
        new(r.Generation, 0, "표시 검증", endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
    private static SavedHiperwallLayout Save(Rig r, DisplayDuration? duration = null, HiperwallPlacement[]? placements = null) =>
        r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, Guid.NewGuid(), 0, 1, "저장 배치", placements ?? [Placement], duration ?? new(DisplayDurationMode.Continuous)));
    private static Task<HiperwallDisplayJob> Show(Rig r, SavedHiperwallLayout l) =>
        r.Service.DisplayHiperwallAsync(r.Admin.Token, new(Guid.NewGuid(), r.Generation, 1, l.Id, l.Version), default);
    private static HiperwallDisplayJob Job(Rig r, Guid id) => r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs.Single(j => j.Request.RequestId == id);

    [Fact]
    public async Task Save_is_local_and_admitted_snapshot_survives_edit_delete_and_restart()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var layout = Save(r); Assert.Empty(f.Commands);
        var job = await Show(r, layout); Assert.Empty(f.Commands);
        var updated = r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, layout.Id, 1, 1, "수정", [Placement with { Layout = new(999, 888, 100, 100) }], new()));
        r.Service.DeleteHiperwallLayout(r.Admin.Token, new(r.Generation, layout.Id, updated.Version));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal("-10.5", f.Commands.Single().Element("x")!.Value);
        Assert.Equal("0", f.Commands.Single().Element("y")!.Value);
        r.Restart(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Single(f.Commands); Assert.True(Job(r, job.Request.RequestId).Outstanding);
        Assert.Empty(r.Service.GetHiperwallDisplays(r.Admin.Token).Layouts);
    }
    [Theory]
    [InlineData(DisplayDurationMode.Default, null)]
    [InlineData(DisplayDurationMode.Timed, 1)]
    [InlineData(DisplayDurationMode.Timed, 86400)]
    [InlineData(DisplayDurationMode.Continuous, null)]
    public async Task Duration_contract_and_duplicate_request(DisplayDurationMode mode, int? seconds)
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var l = Save(r, new(mode, seconds)); var j = await Show(r, l);
        Assert.Equal(mode == DisplayDurationMode.Continuous ? null : r.Clock.GetUtcNow().AddSeconds(seconds ?? 15), j.CloseAt);
        var duplicate = await r.Service.DisplayHiperwallAsync(r.Admin.Token, j.Request, default);
        Assert.Equal(j.Request.RequestId, duplicate.Request.RequestId);
        await r.Service.ReconcileHiperwallDisplaysAsync(default); Assert.Single(f.Commands);
        await Assert.ThrowsAsync<DomainException>(() => r.Service.DisplayHiperwallAsync(r.Admin.Token, j.Request with { Generation = -1 }, default));
    }
    [Theory]
    [InlineData(DisplayDurationMode.Timed, 0)]
    [InlineData(DisplayDurationMode.Timed, 86401)]
    [InlineData(DisplayDurationMode.Default, 2)]
    [InlineData(DisplayDurationMode.Continuous, 2)]
    public async Task Invalid_durations_and_layouts_are_atomic(DisplayDurationMode mode, int? seconds)
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        Assert.Throws<DomainException>(() => Save(r, new(mode, seconds)));
        Assert.Throws<DomainException>(() => Save(r, placements: [Placement, Placement with { Layout = new(0, 0, -1, 1) }]));
        Assert.Empty(r.Service.GetHiperwallDisplays(r.Admin.Token).Layouts); Assert.Empty(f.Commands);
    }
    [Fact]
    public async Task Test_records_deadline_before_send_and_cleans_only_owned_instance_after_crash()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await r.Service.DisplayHiperwallAsync(r.Admin.Token, new(Guid.NewGuid(), r.Generation, 1, TestPlacements: [Placement]), default);
        a.BeforeWrite = c => { var saved = r.Store.Load().HiperwallDisplays.Single(); Assert.Equal(r.Clock.GetUtcNow().AddSeconds(15), saved.CloseAt);
            Assert.True(saved.Targets.Single().OpenAttempted); Assert.Equal(HiperwallSendState.Sending, saved.Targets.Single().OpenState); };
        await r.Service.ReconcileHiperwallDisplaysAsync(default); a.BeforeWrite = null;
        r.Clock.Advance(16); r.Restart(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Job(r, j.Request.RequestId).Outstanding); Assert.Contains("external-1", f.Server.Instances);
        Assert.Equal(2, f.Commands.Count); Assert.Empty(r.Service.GetHiperwallDisplays(r.Admin.Token).Layouts);
    }
    [Fact]
    public async Task Lost_open_and_close_responses_are_reconciled_without_duplicate_open()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe { LoseOpen = true, LoseClose = true }; using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(HiperwallSendState.Unknown, Job(r, j.Request.RequestId).Targets[0].OpenState);
        r.Restart(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(HiperwallSendState.Acknowledged, Job(r, j.Request.RequestId).Targets[0].OpenState); Assert.Single(f.Commands);
        var review = r.Service.ReviewRecovery(r.Admin.Token); r.Service.ApproveRecovery(r.Admin.Token, review.ReviewId); r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        r.Service.StopHiperwallDisplay(r.Admin.Token, new(r.Generation, j.Request.RequestId));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Job(r, j.Request.RequestId).Outstanding); Assert.Equal(2, f.Commands.Count);
    }
    [Fact]
    public async Task Logout_keeps_display_and_next_operator_can_stop_it()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var next = r.Operator("next"); var j = await Show(r, Save(r));
        r.Service.Logout(r.Admin.Token); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Single(f.Commands);
        var generation = r.Service.Acquire(next.Token).Generation;
        var before = r.Service.GetState(next.Token).OutstandingHiperwallDisplays.Single();
        Assert.Equal(r.Admin.Session.Id, before.Requester.Id);
        r.Service.StopHiperwallDisplay(next.Token, new(generation, j.Request.RequestId)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        var after = r.Service.GetHiperwallDisplays(next.Token).Jobs.Single();
        Assert.False(after.Outstanding); Assert.Equal(next.Session.UserId, after.StoppedBy); Assert.Equal(j.Requester, after.Requester);
    }
    [Fact]
    public async Task Cancellation_during_preflight_blocks_open_and_restart_never_replays_pending_targets()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r));
        a.BeforeRead = () => { r.Service.StopHiperwallDisplay(r.Admin.Token, new(r.Generation, j.Request.RequestId)); return Task.CompletedTask; };
        await r.Service.ReconcileHiperwallDisplaysAsync(default); a.BeforeRead = null;
        await r.Service.ReconcileHiperwallDisplaysAsync(default); Assert.Empty(f.Commands); Assert.False(Job(r, j.Request.RequestId).Outstanding);
        await Show(r, Save(r)); r.Restart(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Empty(f.Commands); Assert.All(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs, x => Assert.False(x.Outstanding));
    }
    [Fact]
    public async Task Configuration_change_blocks_pending_open()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r));
        r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 1, "수정", f.Server.Endpoint, HiperwallAuthentication.Token, "3", 3000, null));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Empty(f.Commands); Assert.False(Job(r, j.Request.RequestId).Outstanding);
    }
    [Fact]
    public async Task Shutdown_cleans_free_live_opens_and_displays_but_preserves_external_instances()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        await r.Service.EditHiperwallAsync(r.Admin.Token, new(Guid.NewGuid(), r.Generation, 1, HiperwallEditAction.Open,
            Selector: "uuid", ContentValue: "source-1", ZoneId: "zone-1", Layout: Placement.Layout), default);
        await r.Service.DispatchHiperwallNextAsync(default);
        await Show(r, Save(r)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        // The older LIVE record is reconciled first, then the queued display is dispatched.
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(2, f.Commands.Count);
        r.Service.StopAccepting(); await r.Service.CleanupHiperwallOnShutdownAsync(default);
        Assert.Equal(4, f.Commands.Count); Assert.Contains("external-1", f.Server.Instances);
        Assert.All(r.Store.Load().HiperwallDisplays, j => Assert.False(j.Outstanding));
    }
    [Fact]
    public async Task Failed_shutdown_retains_intent_and_restarts_cleanup()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        a.FailReads = true; r.Service.StopAccepting(); await r.Service.CleanupHiperwallOnShutdownAsync(default);
        Assert.True(r.Store.Load().HiperwallDisplays.Single().StopRequested);
        a.FailReads = false; r.Restart(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Job(r, j.Request.RequestId).Outstanding);
    }
    [Fact]
    public async Task Reused_instance_id_or_changed_controller_never_closes_unrelated_content()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        f.Server.Instances = f.Server.Instances.Replace("<uuid>source-1</uuid>", "<uuid>replacement</uuid>");
        r.Service.StopHiperwallDisplay(r.Admin.Token, new(r.Generation, j.Request.RequestId)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Single(f.Commands); Assert.Equal(DisplayCleanupState.NeedsReview, Job(r, j.Request.RequestId).Targets[0].CleanupState);
    }
    [Fact]
    public async Task Close_404_or_business_error_with_remaining_instance_is_not_success()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        f.CommandResponse = c => c.Attribute("type")?.Value == "close" ? new("<Error><code>404</code></Error>", 404) : null;
        r.Service.StopHiperwallDisplay(r.Admin.Token, new(r.Generation, j.Request.RequestId)); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.True(Job(r, j.Request.RequestId).Outstanding);
        f.CommandResponse = null; r.Clock.Advance(20); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Job(r, j.Request.RequestId).Outstanding);
    }
    [Fact]
    public async Task Missing_source_rejects_whole_batch_and_uuidless_name_is_preserved()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var bad = Save(r, placements: [Placement, Placement with { ContentValue = "gone" }]);
        await Assert.ThrowsAsync<DomainException>(() => Show(r, bad)); Assert.Empty(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs);
        await Show(r, Save(r, placements: [Placement with { Selector = "name", ContentValue = "폴더/이미지 & 지도" }]));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal("폴더/이미지 & 지도", f.Commands.Single().Element("name")!.Value);
    }
    [Fact]
    public async Task Viewer_cannot_save_show_or_stop_and_conflicting_save_is_rejected()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var viewer = r.Operator("view", AccountRole.Viewer); var l = Save(r); var j = await Show(r, l);
        Assert.Throws<DomainException>(() => r.Service.SaveHiperwallLayout(viewer.Token, new(r.Generation, Guid.NewGuid(), 0, 1, "x", [Placement], new())));
        await Assert.ThrowsAsync<DomainException>(() => r.Service.DisplayHiperwallAsync(viewer.Token, new(Guid.NewGuid(), r.Generation, 1, l.Id, l.Version), default));
        Assert.Throws<DomainException>(() => r.Service.StopHiperwallDisplay(viewer.Token, new(r.Generation, j.Request.RequestId)));
        Assert.Throws<DomainException>(() => r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, l.Id, 0, 1, "x", [Placement], new())));
    }
    [Fact]
    public async Task Separate_host_process_crash_recovers_expired_display_without_reopening()
    {
        await using var f = new HiperwallEditorFixture(); await using var host = new HostProcess(); await host.Initialize();
        var (client, _) = await host.Login(); var lease = await HostProcess.Post<Lease>(client, "/api/lease/acquire");
        await HostProcess.Post<HiperwallSettingsView>(client, "/api/hiperwall/settings", new SaveHiperwallRequest(lease.Generation, 0, "표시 검증", f.Server.Endpoint,
            HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        var l = await HostProcess.Post<SavedHiperwallLayout>(client, "/api/hiperwall/layouts/save",
            new SaveHiperwallLayoutRequest(lease.Generation, Guid.NewGuid(), 0, 1, "재시작", [Placement], new(DisplayDurationMode.Timed, 2)));
        await HostProcess.Post<HiperwallDisplayJob>(client, "/api/hiperwall/displays/show", new HiperwallDisplayRequest(Guid.NewGuid(), lease.Generation, 1, l.Id, 1));
        await HostProcess.Until(client, s => s.HiperwallDisplayJobs.Any(j => j.Targets.Any(t => t.OpenState == HiperwallSendState.Acknowledged)));
        await host.Kill(); await Task.Delay(2200); await host.Run();
        var (again, _) = await host.Login(); await HostProcess.Until(again, s => s.OutstandingHiperwallDisplays.Length == 0);
        Assert.Equal(2, f.Commands.Count); Assert.Contains("external-1", f.Server.Instances);
        var view = await again.GetFromJsonAsync<HiperwallDisplayView>("/api/hiperwall/displays", JsonDefaults.Options);
        Assert.Single(view!.Layouts); Assert.False(view.Jobs.Single().Outstanding);
    }

    [Fact]
    public async Task Revoking_original_account_blocks_queued_display()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var op = r.Operator("operator"); var l = Save(r);
        r.Service.Release(r.Admin.Token, r.Generation); var generation = r.Service.Acquire(op.Token).Generation;
        var j = await r.Service.DisplayHiperwallAsync(op.Token, new(Guid.NewGuid(), generation, 1, l.Id, l.Version), default);
        r.Service.Release(op.Token, generation); r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        r.Service.UpdateAccount(r.Admin.Token, new(r.Generation, op.Session.UserId, false, true, []));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Job(r, j.Request.RequestId).Outstanding); Assert.Empty(f.Commands);
    }
    [Fact]
    public async Task Read_only_reconciliation_does_not_invalidate_recovery_review()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        await Show(r, Save(r)); await r.Service.ReconcileHiperwallDisplaysAsync(default); r.Restart();
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        var review = r.Service.ReviewRecovery(r.Admin.Token);
        r.Clock.Advance(5); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(LeaseMode.Free, r.Service.ApproveRecovery(r.Admin.Token, review.ReviewId).Mode);
    }
    [Fact]
    public async Task Live_edit_cannot_reuse_display_request_id_and_escape_ownership_tracking()
    {
        await using var f = new HiperwallEditorFixture(); using var a = new Probe(); using var r = new Rig(a, new Secrets()); Configure(r, f.Server.Endpoint);
        var j = await Show(r, Save(r));
        await Assert.ThrowsAsync<DomainException>(() => r.Service.EditHiperwallAsync(r.Admin.Token,
            new(j.Request.RequestId, r.Generation, 1, HiperwallEditAction.Open, Selector: "uuid", ContentValue: "source-1", ZoneId: "zone-1", Layout: Placement.Layout), default));
        Assert.Empty(f.Commands);
    }
}
