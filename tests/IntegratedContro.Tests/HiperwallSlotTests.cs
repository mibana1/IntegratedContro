using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class HiperwallSlotTests
{
    private sealed class Secrets : ICredentialStore
    {
        public Guid Save(string secret) => Guid.NewGuid();
        public string Read(Guid reference) => FakeHiperwallServer.FixtureSecret;
    }
    private sealed class Probe : IHiperwallReader, IHiperwallWriter, IDisposable
    {
        private readonly HiperwallHttpReader _inner = new();
        public bool LoseOpen;
        public Action? BeforeRead;
        public Task<HiperwallReading> ReadAsync(HiperwallConfiguration c, string? s, CancellationToken ct)
        { BeforeRead?.Invoke(); return _inner.ReadAsync(c, s, ct); }
        public async Task<HiperwallWriteResult> WriteAsync(HiperwallConfiguration c, string? s, HiperwallWireCommand command, CancellationToken ct)
        {
            var result = await _inner.WriteAsync(c, s, command, ct);
            return LoseOpen && command.Action == HiperwallEditAction.Open ? new(HiperwallSendState.Unknown, "lost reply") : result;
        }
        public void Dispose() => _inner.Dispose();
    }
    private const string Mixed = """
        <Objects>
        <Object type="stream"><name>폴더/영상 &amp; 소리</name><uuid>source-1</uuid><zone>zone-1</zone>
          <Instance><id>external-1</id><position>-960.25,15.5</position><size>640.25,360.5</size><rotation>0</rotation><audio>37,muted</audio></Instance>
          <Instance><id>external-2</id><position>-300,0</position><size>320,180</size><rotation>0</rotation><audio>75,unmuted</audio></Instance>
        </Object>
        <Object type="image"><name>폴더/이미지 &amp; 지도</name><zone>zone-2</zone>
          <Instance><id>external-3</id><position>900,0</position><size>640,480</size><rotation>0</rotation><audio>100,unmuted</audio></Instance>
        </Object></Objects>
        """;
    private static void Configure(Rig r, string endpoint) => r.Service.SaveHiperwallSettings(r.Admin.Token,
        new(r.Generation, 0, "슬롯 검증", endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
    private static async Task<SaveHiperwallSlotRequest> SaveRequest(Rig r, int number = 1)
    {
        var view = await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        return new(r.Generation, number, r.Service.GetState(r.Admin.Token).HiperwallSlots.SingleOrDefault(s => s.Number == number)?.Version ?? 0,
            1, HiperwallEditing.Revision(view.Instances.Items));
    }
    private static async Task<HiperwallSlot> Capture(Rig r, int number = 1) =>
        await r.Service.SaveHiperwallSlotAsync(r.Admin.Token, await SaveRequest(r, number), default);
    private static async Task<HiperwallEditReceipt> Restore(Rig r, HiperwallSlot slot)
    {
        var view = await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        return await r.Service.EditHiperwallAsync(r.Admin.Token,
            new HiperwallEditRequest(Guid.NewGuid(), r.Generation, 1, HiperwallEditAction.RestoreSlot,
                ExpectedRevision: HiperwallEditing.Revision(view.Instances.Items)) { SlotNumber = slot.Number, SlotVersion = slot.Version }, default);
    }
    private static async Task<HiperwallEditReceipt> Drain(Rig r, Guid id)
    {
        for (var i = 0; i < 20 && r.Service.GetHiperwallEdit(r.Admin.Token, id).Active; i++)
            await r.Service.DispatchHiperwallNextAsync(default);
        var result = r.Service.GetHiperwallEdit(r.Admin.Token, id); Assert.False(result.Active); return result;
    }
    [Fact]
    public async Task Missing_zone_metadata_uses_unique_centers_for_capture_and_every_restore_step()
    {
        await using var f = new HiperwallEditorFixture { OmitInstanceZones = true };
        using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint);
        f.Server.Instances = Mixed.Replace("-960.25,15.5", "-960.25,-15.5").Replace("640,480", "4000,2200");
        var slot = await Capture(r);
        Assert.Equal(new[] { "zone-1", "zone-1", "zone-2" }, slot.Placements.Select(p => p.ZoneId));
        Assert.Equal(new(-960.25, 15.5, 640.25, 360.5), slot.Placements[0].Layout);
        Assert.Equal(new(900, 0, 4000, 2200), slot.Placements[2].Layout);
        Assert.Empty(f.Commands);
        var restored = await Drain(r, (await Restore(r, slot)).Request.RequestId);
        Assert.All(restored.Steps, s => Assert.Equal(HiperwallSendState.Acknowledged, s.State));
        Assert.Equal(new[] { "close", "close", "close", "open", "open", "open" },
            f.Commands.Select(c => c.Attribute("type")!.Value));
        var view = await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        foreach (var (item, expected) in view.Instances.Items.Zip(slot.Placements))
        {
            Assert.False(item.Fields.ContainsKey("content.zone"));
            Assert.True(HiperwallGeometry.TryInstance(item, out var rect, out _));
            Assert.Equal(expected.Layout, HiperwallLayout.From(rect));
        }
        var recaptured = await Capture(r);
        Assert.Equal(slot.Placements, recaptured.Placements);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("overlap")]
    [InlineData("boundary")]
    [InlineData("invalid_zone")]
    public async Task Unresolved_zone_reports_instance_and_preserves_saved_slot_without_writes(string kind)
    {
        await using var f = new HiperwallEditorFixture();
        using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); f.Server.Instances = Mixed;
        var saved = await Capture(r);
        if (kind == "invalid_zone") f.Server.Instances = Mixed.Replace("<zone>zone-2</zone>", "<zone>missing</zone>");
        else
        {
            f.Server.Instances = Mixed.Replace("<zone>zone-2</zone>", "");
            if (kind == "outside") f.Server.Instances = f.Server.Instances.Replace("900,0", "5000,-5000");
            if (kind == "overlap") f.Server.Walls = f.Server.Walls.Replace("<left>-1920.5</left>", "<left>0</left>");
            if (kind == "boundary")
            {
                f.Server.Walls = f.Server.Walls.Replace("<left>-1920.5</left>", "<left>-1920</left>");
                f.Server.Instances = f.Server.Instances.Replace("900,0", "0,0");
            }
        }
        var error = await Assert.ThrowsAsync<DomainException>(() => Capture(r));
        Assert.Equal("hiperwall_zone_missing", error.Code);
        Assert.Contains("external-3", error.Message);
        Assert.Contains("폴더/이미지 & 지도", error.Message);
        var actual = r.Service.GetState(r.Admin.Token).HiperwallSlots.Single();
        Assert.Equal(saved.Version, actual.Version); Assert.Equal(saved.Placements, actual.Placements);
        Assert.Empty(f.Commands);
    }

    [Fact]
    public async Task Missing_zone_on_open_still_rejects_external_geometry_change_before_next_open()
    {
        await using var f = new HiperwallEditorFixture { OmitInstanceZones = true };
        using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint);
        f.Server.Instances = Mixed.Replace("-960.25,15.5", "-960.25,-15.5");
        var slot = await Capture(r); var receipt = await Restore(r, slot);
        for (var i = 0; i < 4; i++) await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Equal(4, f.Commands.Count);
        f.Server.Instances = f.Server.Instances.Replace("640.25,360.5", "641.25,360.5");
        var result = await Drain(r, receipt.Request.RequestId);
        Assert.Equal(4, f.Commands.Count);
        Assert.All(result.Steps.Skip(4), s => Assert.Equal(HiperwallSendState.Rejected, s.State));
    }

    [Fact]
    public async Task Capture_overwrite_delete_preserve_live_and_persist_monotonic_slot_versions()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); f.Server.Instances = Mixed;
        var slot = await Capture(r);
        Assert.True(r.Service.GetState(r.Admin.Token).HiperwallSlotsSupported);
        Assert.Equal(3, slot.Placements.Length); Assert.Equal(2, slot.Placements.Count(p => p.ContentValue == "source-1"));
        Assert.Equal(new(-960.25, -15.5, 640.25, 360.5), slot.Placements[0].Layout);
        Assert.Equal(37, slot.Placements[0].Volume); Assert.True(slot.Placements[0].Muted);
        Assert.Equal("name", slot.Placements[2].Selector); Assert.Equal("zone-2", slot.Placements[2].ZoneId);
        var old = await SaveRequest(r); slot = await Capture(r);
        Assert.Equal(2, slot.Version);
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            r.Service.SaveHiperwallSlotAsync(r.Admin.Token, old, default))).Code);
        Assert.True(r.Service.DeleteHiperwallSlot(r.Admin.Token, new(r.Generation, 1, 2)));
        Assert.True(r.Service.GetState(r.Admin.Token).HiperwallSlots.Single().IsEmpty);
        Assert.Empty(f.Commands); Assert.Equal(Mixed, f.Server.Instances);
        r.Service.Release(r.Admin.Token, r.Generation); r.Restart(); r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        Assert.Equal(3, r.Service.GetState(r.Admin.Token).HiperwallSlots.Single().Version);
        slot = await Capture(r); Assert.Equal(4, slot.Version);
        Rig.Reject("version_conflict", () => r.Service.DeleteHiperwallSlot(r.Admin.Token, new(r.Generation, 1, 2)));
        var audit = r.Service.GetState(r.Admin.Token).Audit;
        Assert.Contains(audit, a => a.Action == "HiperwallSlotSaved" && a.Message!.Contains("3개"));
        Assert.Contains(audit, a => a.Action == "HiperwallSlotDeleted" && a.EventName == "Hiperwall 슬롯 삭제");
    }
    [Fact]
    public async Task Restore_replaces_all_instances_with_frozen_sources_zones_geometry_and_tracks_every_open()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); f.Server.Instances = Mixed;
        var slot = await Capture(r);
        f.Server.Instances = Mixed.Replace("640.25,360.5", "123,45").Replace("-300,0", "100,90");
        var receipt = await Restore(r, slot);
        Assert.Empty(f.Commands); Assert.Equal(6, receipt.Steps.Count);
        Assert.Equal(receipt.Request.RequestId, (await r.Service.EditHiperwallAsync(r.Admin.Token, receipt.Request, default)).Request.RequestId);
        r.Service.DeleteHiperwallSlot(r.Admin.Token, new(r.Generation, slot.Number, slot.Version));
        r.Service.Release(r.Admin.Token, r.Generation);
        var result = await Drain(r, receipt.Request.RequestId);
        Assert.All(result.Steps, s => Assert.Equal(HiperwallSendState.Acknowledged, s.State));
        Assert.Equal(new[] { "close", "close", "close", "open", "open", "open" },
            f.Commands.Select(c => c.Attribute("type")!.Value));
        var view = await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        Assert.Equal(3, view.Instances.Items.Length); Assert.DoesNotContain(view.Instances.Items, i => i.Id!.StartsWith("external"));
        foreach (var (item, expected) in view.Instances.Items.Zip(slot.Placements))
        {
            Assert.True(HiperwallGeometry.TryInstance(item, out var rect, out _)); Assert.Equal(expected.Layout, HiperwallLayout.From(rect));
            Assert.Equal(expected.ZoneId, item.Fields["content.zone"]);
        }
        var tracked = r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs.Single();
        Assert.Equal(3, tracked.Targets.Count); Assert.Null(tracked.CloseAt); Assert.Equal(DisplayDurationMode.Continuous, tracked.Duration.Mode);
        r.Service.StopAccepting(); await r.Service.CleanupHiperwallOnShutdownAsync(default);
        Assert.All(r.Store.Load().HiperwallDisplays.Single().Targets, t => Assert.False(t.Outstanding));
        Assert.DoesNotContain("Instance", f.Server.Instances);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("zone")]
    [InlineData("geometry")]
    public async Task Invalid_capture_is_atomic_and_missing_restore_targets_do_not_clear_live(string invalid)
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); var slot = await Capture(r);
        if (invalid == "source") f.Server.Contents = "<Objects/>";
        else if (invalid == "zone") f.Server.Instances = f.Server.Instances.Replace("<zone>zone-1</zone>", "<zone>missing</zone>");
        else f.Server.Instances = f.Server.Instances.Replace("<rotation>0</rotation>", "<rotation>90</rotation>");
        await Assert.ThrowsAsync<DomainException>(() => Capture(r));
        Assert.Equal(1, r.Service.GetState(r.Admin.Token).HiperwallSlots.Single().Version);
        if (invalid == "source") await Assert.ThrowsAsync<DomainException>(() => Restore(r, slot));
        Assert.Empty(f.Commands);
    }
    [Fact]
    public async Task Save_delete_and_restore_require_permissions_generation_versions_and_observed_revision()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); var request = await SaveRequest(r);
        Assert.Equal("lease_required", (await Assert.ThrowsAsync<DomainException>(() =>
            r.Service.SaveHiperwallSlotAsync(r.Admin.Token, request with { Generation = -1 }, default))).Code);
        Assert.Equal("invalid_slot", (await Assert.ThrowsAsync<DomainException>(() =>
            r.Service.SaveHiperwallSlotAsync(r.Admin.Token, request with { Number = 7 }, default))).Code);
        f.Server.Instances = f.Server.Instances.Replace("640,360", "800,600");
        Assert.Equal("hiperwall_revision", (await Assert.ThrowsAsync<DomainException>(() =>
            r.Service.SaveHiperwallSlotAsync(r.Admin.Token, request, default))).Code);
        var slot = await Capture(r);
        var viewer = r.Operator("viewer", AccountRole.Viewer);
        Rig.Reject("lease_required", () => r.Service.DeleteHiperwallSlot(viewer.Token, new(r.Generation, 1, slot.Version)));
        var limited = r.Operator("limited", all: false);
        r.Service.Release(r.Admin.Token, r.Generation); var lease = r.Service.Acquire(limited.Token);
        Assert.Equal("hiperwall_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            r.Service.SaveHiperwallSlotAsync(limited.Token, request with { Generation = lease.Generation }, default))).Code);
        Rig.Reject("hiperwall_scope", () => r.Service.DeleteHiperwallSlot(limited.Token, new(lease.Generation, 1, slot.Version)));
        Assert.Empty(f.Commands);
    }
    [Fact]
    public async Task Acknowledged_close_without_observed_removal_stops_before_opening_saved_content()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); var slot = await Capture(r); var receipt = await Restore(r, slot);
        f.CommandResponse = c => c.Attribute("type")!.Value == "close" ? new("<Response />") : null;
        var result = await Drain(r, receipt.Request.RequestId);
        Assert.Equal(HiperwallSendState.Unknown, result.Steps[0].State);
        Assert.Equal(HiperwallSendState.Rejected, result.Steps[1].State);
        Assert.Single(f.Commands); Assert.Contains("external-1", f.Server.Instances);
    }
    [Fact]
    public async Task External_change_between_close_steps_stops_the_remaining_replacement()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); f.Server.Instances = Mixed; var slot = await Capture(r); var receipt = await Restore(r, slot);
        await r.Service.DispatchHiperwallNextAsync(default);
        f.Server.Instances = f.Server.Instances.Replace("320,180", "333,222");
        var result = await Drain(r, receipt.Request.RequestId);
        Assert.Single(f.Commands); Assert.All(result.Steps.Skip(1), s => Assert.Equal(HiperwallSendState.Rejected, s.State));
    }
    [Fact]
    public async Task Lost_open_reply_stops_following_steps_and_restart_preserves_owned_instance_without_replay()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); f.Server.Instances = Mixed; var slot = await Capture(r); var receipt = await Restore(r, slot);
        adapter.LoseOpen = true;
        var result = await Drain(r, receipt.Request.RequestId);
        Assert.Equal(HiperwallSendState.Unknown, result.Steps[3].State);
        Assert.All(result.Steps.Skip(4), s => Assert.Equal(HiperwallSendState.Rejected, s.State));
        Assert.Equal(4, f.Commands.Count); Assert.Single(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs.Single().Targets);
        r.Restart(); await r.Service.DispatchHiperwallNextAsync(default); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(4, f.Commands.Count);
        Assert.Equal(HiperwallSendState.Unknown, r.Service.GetHiperwallEdit(r.Admin.Token, receipt.Request.RequestId).Steps[3].State);
    }
    [Fact]
    public async Task Cancel_during_preflight_prevents_any_write_and_restart_does_not_resume_partial_restore()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); var slot = await Capture(r); var receipt = await Restore(r, slot);
        adapter.BeforeRead = () => r.Service.CancelHiperwallEdit(r.Admin.Token, new(r.Generation, receipt.Request.RequestId));
        await r.Service.DispatchHiperwallNextAsync(default); adapter.BeforeRead = null;
        Assert.Empty(f.Commands); Assert.False(r.Service.GetHiperwallEdit(r.Admin.Token, receipt.Request.RequestId).Active);
        receipt = await Restore(r, slot); await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Single(f.Commands); r.Restart(); await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Single(f.Commands); Assert.False(r.Service.GetHiperwallEdit(r.Admin.Token, receipt.Request.RequestId).Active);
    }
    [Fact]
    public async Task Restore_reserves_the_write_sequence_and_pending_displays_must_finish_first()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new Probe(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); var slot = await Capture(r);
        var layout = r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, Guid.NewGuid(), 0, 1,
            "기존 표시", slot.Placements, new(DisplayDurationMode.Continuous)));
        var display = await r.Service.DisplayHiperwallAsync(r.Admin.Token, new(Guid.NewGuid(), r.Generation, 1, layout.Id, layout.Version), default);
        Assert.Equal("hiperwall_busy", (await Assert.ThrowsAsync<DomainException>(() => Restore(r, slot))).Code);
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        var restore = await Restore(r, slot);
        Assert.Equal("hiperwall_busy", (await Assert.ThrowsAsync<DomainException>(() =>
            r.Service.DisplayHiperwallAsync(r.Admin.Token, new(Guid.NewGuid(), r.Generation, 1, layout.Id, layout.Version), default))).Code);
        r.Service.StopHiperwallDisplay(r.Admin.Token, new(r.Generation, display.Request.RequestId));
        var count = f.Commands.Count; await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(count, f.Commands.Count); // Scheduled cleanup waits until the replacement releases the sequence.
        var result = await Drain(r, restore.Request.RequestId);
        Assert.All(result.Steps, step => Assert.Equal(HiperwallSendState.Acknowledged, step.State));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs.Single(j => j.Request.RequestId == display.Request.RequestId).Outstanding);
    }
    [Fact]
    public async Task Restore_rejects_missing_zone_changed_configuration_and_host_only_wire_action()
    {
        await using var f = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader(); using var r = new Rig(adapter, new Secrets());
        Configure(r, f.Server.Endpoint); var slot = await Capture(r);
        var savedWalls = f.Server.Walls; f.Server.Walls = savedWalls.Replace("zone-1", "changed-zone");
        Assert.Equal("hiperwall_zone_missing", (await Assert.ThrowsAsync<DomainException>(() => Restore(r, slot))).Code);
        f.Server.Walls = savedWalls;
        r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 1, "새 설정", f.Server.Endpoint,
            HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        var view = await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        Assert.Equal("slot_changed", (await Assert.ThrowsAsync<DomainException>(() => r.Service.EditHiperwallAsync(r.Admin.Token,
            new HiperwallEditRequest(Guid.NewGuid(), r.Generation, 2, HiperwallEditAction.RestoreSlot,
                ExpectedRevision: HiperwallEditing.Revision(view.Instances.Items)) { SlotNumber = 1, SlotVersion = slot.Version }, default))).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.WriteAsync(
            new(2, "검증", f.Server.Endpoint, HiperwallAuthentication.Token, "3", 3000, null),
            FakeHiperwallServer.FixtureSecret, new(HiperwallEditAction.RestoreSlot, "external-1"), default));
        Assert.Empty(f.Commands);
    }
}
