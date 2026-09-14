using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;
using System.Xml.Linq;

namespace IntegratedContro.Tests;

public sealed class HiperwallEditingTests
{
    private sealed class Secrets : ICredentialStore
    {
        public Guid Save(string secret) => Guid.NewGuid();
        public string Read(Guid reference) => FakeHiperwallServer.FixtureSecret;
    }
    private static void Configure(Rig rig, string endpoint) => rig.Service.SaveHiperwallSettings(rig.Admin.Token,
        new(rig.Generation, 0, "편집 검증", endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
    private static HiperwallEditRequest Open(Rig rig) => new(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.Open,
        Selector: "uuid", ContentValue: "source-1", ZoneId: "zone-1", Layout: new(-10.5, 0, 2560, 1440));
    private static async Task<HiperwallEditReceipt> Send(Rig rig, HiperwallEditRequest request)
    {
        await rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default);
        while (rig.Service.GetHiperwallEdit(rig.Admin.Token, request.RequestId).Active) await rig.Service.DispatchHiperwallNextAsync(default);
        return rig.Service.GetHiperwallEdit(rig.Admin.Token, request.RequestId);
    }
    [Fact]
    public async Task Open_move_resize_audio_and_close_use_explicit_targets_and_preserve_native_size()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var opened = await Send(rig, Open(rig));
        Assert.Equal(HiperwallSendState.Acknowledged, opened.Steps.Single().State);
        var id = opened.Steps.Single().Command.InstanceId;
        var xml = fixture.Commands.Single();
        Assert.Equal("2560", xml.Element("boundsw")!.Value); Assert.Equal("0", xml.Element("y")!.Value);
        var state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        var item = state.Instances.Items.Single(i => i.Id == id);
        var change = new HiperwallEditRequest(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.Change, id,
            ZoneId: "zone-2", Layout: new(-250.25, 999.5, 900.5, 450.25), Volume: 42, Muted: true, ExpectedRevision: HiperwallEditing.Revision([item]));
        Assert.Equal(HiperwallSendState.Acknowledged, (await Send(rig, change)).Steps.Single().State);
        xml = fixture.Commands.Last(); Assert.Equal("-999.5", xml.Element("y")!.Value); Assert.Equal("0", xml.Element("queue")!.Value);
        state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default); item = state.Instances.Items.Single(i => i.Id == id);
        Assert.True(HiperwallEditing.TryAudio(item, out var volume, out var muted)); Assert.Equal(42, volume); Assert.True(muted);
        await Send(rig, change with { RequestId = Guid.NewGuid(), Layout = null, ZoneId = null, Volume = 61, Muted = null, ExpectedRevision = HiperwallEditing.Revision([item]) });
        Assert.Null(fixture.Commands.Last().Element("mute"));
        state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default); item = state.Instances.Items.Single(i => i.Id == id);
        await Send(rig, new(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.Close, id, ExpectedRevision: HiperwallEditing.Revision([item])));
        Assert.DoesNotContain(id!, fixture.Server.Instances); Assert.Contains("external-1", fixture.Server.Instances);
    }
    [Fact]
    public async Task Name_without_uuid_preserves_full_path_and_xml_characters()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        await Send(rig, Open(rig) with { Selector = "name", ContentValue = "폴더/이미지 & 지도" });
        Assert.Equal("폴더/이미지 & 지도", fixture.Commands.Single().Element("name")!.Value);
        Assert.Contains("&amp;", fixture.Commands.Single().ToString());
    }
    [Fact]
    public async Task Viewer_scoped_operator_no_lease_and_stale_settings_cannot_send()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var viewer = rig.Operator("viewer", AccountRole.Viewer); var scoped = rig.Operator("scoped", all: false);
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(viewer.Token, Open(rig), default));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(rig.Admin.Token, Open(rig) with { ConfigurationVersion = 2 }, default));
        rig.Service.Release(rig.Admin.Token, rig.Generation);
        rig.Generation = rig.Service.Acquire(scoped.Token).Generation;
        Assert.Equal("hiperwall_scope", (await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(scoped.Token, Open(rig), default))).Code);
        Assert.Empty(fixture.Commands);
    }
    [Fact]
    public async Task Admission_rejects_external_changes_and_close_all_uses_confirmed_set()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        var request = new HiperwallEditRequest(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.CloseAll, ExpectedRevision: HiperwallEditing.Revision(state.Instances.Items));
        await Send(rig, Open(rig));
        Assert.Equal("hiperwall_revision", (await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default))).Code);
        state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        var result = await Send(rig, request with { ExpectedRevision = HiperwallEditing.Revision(state.Instances.Items) });
        Assert.Equal(2, result.Steps.Count); Assert.All(result.Steps, s => Assert.Equal(HiperwallSendState.Acknowledged, s.State));
        Assert.Empty(XElement.Parse(fixture.Server.Instances).Elements());
    }
    [Fact]
    public async Task Duplicate_request_never_resends_and_accepted_edit_survives_logout()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var request = Open(rig);
        await rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default);
        await rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default);
        rig.Service.Logout(rig.Admin.Token);
        await rig.Service.DispatchHiperwallNextAsync(default);
        Assert.Single(fixture.Commands);
        var token = rig.Login().Token;
        var receipt = await rig.Service.EditHiperwallAsync(token, request, default);
        Assert.Equal(HiperwallSendState.Acknowledged, receipt.Steps.Single().State);
        Assert.Single(fixture.Commands);
    }
    [Fact]
    public async Task Rejected_response_and_lost_response_are_distinct_without_retry()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        fixture.CommandResponse = _ => new("<Response><Error><code>403</code><reason>SECRET</reason></Error></Response>");
        var rejected = await Send(rig, Open(rig));
        Assert.Equal(HiperwallSendState.Rejected, rejected.Steps.Single().State); Assert.DoesNotContain("SECRET", rejected.Steps.Single().Message);
        fixture.CommandResponse = _ => new("", Disconnect: true);
        var lost = await Send(rig, Open(rig));
        Assert.Equal(HiperwallSendState.Unknown, lost.Steps.Single().State);
        await rig.Service.DispatchHiperwallNextAsync(default); Assert.Equal(2, fixture.Commands.Count);
        rig.Restart(); Assert.Equal(HiperwallSendState.Unknown, rig.Service.GetHiperwallEdit(rig.Admin.Token, lost.Request.RequestId).Steps.Single().State);
    }
    [Fact]
    public async Task Restart_and_cancel_preserve_records_without_replaying_pending_commands()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var request = Open(rig); await rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default);
        rig.Restart(); await rig.Service.DispatchHiperwallNextAsync(default);
        Assert.Empty(fixture.Commands); Assert.Equal(HiperwallSendState.Rejected, rig.Service.GetHiperwallEdit(rig.Admin.Token, request.RequestId).Steps.Single().State);
    }
    [Fact]
    public async Task Dispatch_blocks_changed_targets_revoked_scope_and_cancelled_pending_work()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        var request = new HiperwallEditRequest(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.Close, "external-1", ExpectedRevision: HiperwallEditing.Revision(state.Instances.Items));
        await rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default);
        fixture.Server.Instances = fixture.Server.Instances.Replace("640,360", "800,450");
        await rig.Service.DispatchHiperwallNextAsync(default);
        Assert.Equal(HiperwallSendState.Rejected, rig.Service.GetHiperwallEdit(rig.Admin.Token, request.RequestId).Steps.Single().State);
        var open = Open(rig); await rig.Service.EditHiperwallAsync(rig.Admin.Token, open, default);
        rig.Service.CancelHiperwallEdit(rig.Admin.Token, new(rig.Generation, open.RequestId)); await rig.Service.DispatchHiperwallNextAsync(default);
        Assert.Empty(fixture.Commands);
        open = Open(rig); await rig.Service.EditHiperwallAsync(rig.Admin.Token, open, default);
        rig.Service.UpdateAccount(rig.Admin.Token, new(rig.Generation, rig.Admin.Session.UserId, true, false, []));
        await rig.Service.DispatchHiperwallNextAsync(default); Assert.Empty(fixture.Commands);
        Assert.Equal(HiperwallSendState.Rejected, rig.Service.GetHiperwallEdit(rig.Admin.Token, open.RequestId).Steps.Single().State);
    }
    [Fact]
    public async Task Close_all_records_partial_failure_and_never_uses_a_remote_blanket_close()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        await Send(rig, Open(rig));
        var state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        fixture.CommandResponse = x => x.Element("id")?.Value == "external-1" ? new("<Error><code>403</code></Error>") : null;
        var result = await Send(rig, new(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.CloseAll, ExpectedRevision: HiperwallEditing.Revision(state.Instances.Items)));
        Assert.Single(result.Steps, s => s.State == HiperwallSendState.Rejected); Assert.Single(result.Steps, s => s.State == HiperwallSendState.Acknowledged);
        Assert.All(fixture.Commands.Skip(1), c => { Assert.Equal("close", c.Attribute("type")!.Value); Assert.NotNull(c.Element("id")); });
    }
    [Fact]
    public async Task Close_lost_response_requires_fresh_absence_confirmation()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        var state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        fixture.CommandResponse = _ => { fixture.Server.Instances = "<Objects/>"; return new("", 404); };
        var result = await Send(rig, new(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.Close, "external-1", ExpectedRevision: HiperwallEditing.Revision(state.Instances.Items)));
        Assert.Equal(HiperwallSendState.Acknowledged, result.Steps.Single().State); Assert.Contains("인스턴스 없음", result.Steps.Single().Message);
        Assert.Single(fixture.Commands);
    }
    [Fact]
    public async Task Name_fallback_rejects_a_duplicate_even_when_other_match_has_uuid()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        fixture.Server.Contents = fixture.Server.Contents.Replace("폴더/이미지 &amp; 지도", "폴더/영상 &amp; 소리");
        Assert.Equal("hiperwall_content_missing", (await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(rig.Admin.Token,
            Open(rig) with { Selector = "name", ContentValue = "폴더/영상 & 소리" }, default))).Code);
        Assert.Empty(fixture.Commands);
    }
    [Fact]
    public async Task Shadow_and_missing_audio_cannot_be_used_as_write_support()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var rig = new Rig(adapter, new Secrets()); Configure(rig, fixture.Server.Endpoint);
        fixture.Server.Hello = "Hiperwall,2026 R2,Token,Shadow";
        Assert.Equal("hiperwall_not_writable", (await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(rig.Admin.Token, Open(rig), default))).Code);
        fixture.Server.Hello = "Hiperwall,2026 R2,Token,Primary";
        fixture.Server.Instances = fixture.Server.Instances.Replace("<audio>75,unmuted</audio>", "");
        var state = await rig.Service.RefreshHiperwallAsync(rig.Admin.Token, false, default);
        var request = new HiperwallEditRequest(Guid.NewGuid(), rig.Generation, 1, HiperwallEditAction.Change, "external-1", Volume: 50, ExpectedRevision: HiperwallEditing.Revision(state.Instances.Items));
        Assert.Equal("hiperwall_audio", (await Assert.ThrowsAsync<DomainException>(() => rig.Service.EditHiperwallAsync(rig.Admin.Token, request, default))).Code);
        Assert.Empty(fixture.Commands);
    }
    [Fact]
    public async Task Detailed_fields_are_requested_and_unsupported_fields_fall_back_to_basic_read_only_lists()
    {
        await using var server = new FakeHiperwallServer(); using var adapter = new HiperwallHttpReader();
        var fields = new List<string>();
        server.Handler = request =>
        {
            if (request.Method == "POST" && XElement.Parse(request.Body).Element("action")?.Element("fields") is { } field)
            { fields.Add(field.Value); return Task.FromResult(new FakeHiperwallServer.Response("<Error><code>400</code></Error>")); }
            return Task.FromResult(server.Default(request));
        };
        var state = await adapter.ReadAsync(new(1, "fixture", server.Endpoint, HiperwallAuthentication.Token, "3", 3000, null), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(HiperwallConnectionState.Connected, state.State);
        Assert.Contains("label,width,height,said,zone", fields);
        Assert.Contains(fields, f => f.Contains("instances(position,size") && f.Contains("audio"));
        Assert.Equal(new[] { "GET /hello", "list", "walls", "list/open" }, server.Operations.ToArray());
    }
    [Fact]
    public void Gesture_geometry_keeps_aspect_and_zone_outside_coordinates()
    {
        var layout = new HiperwallLayout(100, 200, 640, 360);
        var moved = HiperwallEditing.Move(layout, -1000.5, 10.25);
        Assert.Equal(-900.5, moved.X); Assert.Equal(210.25, moved.Y); Assert.Equal(640, moved.Width);
        var resized = HiperwallEditing.Resize(layout, 640, 360, true);
        Assert.Equal(1280, resized.Width); Assert.Equal(720, resized.Height);
        Assert.Equal(layout.X - layout.Width / 2, resized.X - resized.Width / 2);
        var snapped = HiperwallEditing.Move(layout, 15, 9, new(-100, 0, 400, 400), 4, 4);
        Assert.Equal(100, snapped.X); Assert.Equal(200, snapped.Y);
    }
}
