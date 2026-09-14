using IntegratedContro.Core;
using IntegratedContro.Application;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class HandoverTests
{
    private sealed class Secrets : ICredentialStore
    {
        public Guid Save(string secret) => Guid.NewGuid();
        public string Read(Guid reference) => FakeHiperwallServer.FixtureSecret;
    }
    private static void Configure(Rig r, string endpoint) => r.Service.SaveHiperwallSettings(r.Admin.Token,
        new(r.Generation, 0, "교대 검증", endpoint, HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
    private static Task<HiperwallEditReceipt> Open(Rig r) => r.Service.EditHiperwallAsync(r.Admin.Token,
        new(Guid.NewGuid(), r.Generation, 1, HiperwallEditAction.Open, Selector: "uuid", ContentValue: "source-1",
            ZoneId: "zone-1", Layout: new(0, 0, 640, 360)), default);

    [Fact]
    public async Task Common_state_exposes_pending_handover_to_same_account_session_and_cancel_updates_it()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture.Server.Endpoint);
        var receipt = await Open(r);
        r.Service.Release(r.Admin.Token, r.Generation);
        var next = r.Login(pc: "next PC");
        var view = r.Service.GetState(next.Token);
        var visible = Assert.Single(view.OutstandingHiperwallEdits);
        Assert.Equal(receipt.Request.RequestId, visible.Request.RequestId);
        Assert.Equal(r.Admin.Session.Id, visible.Requester.Id);
        Assert.NotEqual(next.Session.Id, visible.Requester.Id);
        Assert.Equal(next.Session.UserId, visible.Requester.UserId);
        var generation = r.Service.Acquire(next.Token).Generation;
        Rig.Reject("lease_required", () => r.Service.CancelHiperwallEdit(r.Admin.Token, new(generation, receipt.Request.RequestId)));
        r.Service.CancelHiperwallEdit(next.Token, new(generation, receipt.Request.RequestId));
        Assert.Empty(r.Service.GetState(next.Token).OutstandingHiperwallEdits);
        Assert.Equal(HiperwallSendState.Rejected, r.Service.GetHiperwallEdit(next.Token, receipt.Request.RequestId).Steps.Single().State);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task Viewer_and_scoped_owner_see_work_but_cannot_cancel_it()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture.Server.Endpoint);
        var viewer = r.Operator("viewer", AccountRole.Viewer);
        var scoped = r.Operator("scoped", all: false);
        var receipt = await Open(r);
        r.Service.Release(r.Admin.Token, r.Generation);
        Assert.Single(r.Service.GetState(viewer.Token).OutstandingHiperwallEdits);
        Rig.Reject("lease_required", () => r.Service.CancelHiperwallEdit(viewer.Token, new(r.Generation, receipt.Request.RequestId)));
        var generation = r.Service.Acquire(scoped.Token).Generation;
        Assert.Single(r.Service.GetState(scoped.Token).OutstandingHiperwallEdits);
        Assert.False(r.Service.GetState(scoped.Token).CanControlHiperwall);
        Rig.Reject("hiperwall_scope", () => r.Service.CancelHiperwallEdit(scoped.Token, new(generation, receipt.Request.RequestId)));
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task Completion_disappears_from_outstanding_but_unknown_remains_without_resending()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture.Server.Endpoint);
        await Open(r); await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Empty(r.Service.GetState(r.Admin.Token).OutstandingHiperwallEdits);
        fixture.CommandResponse = _ => new("", Disconnect: true);
        var unknown = await Open(r); await r.Service.DispatchHiperwallNextAsync(default);
        var receipt = Assert.Single(r.Service.GetState(r.Admin.Token).OutstandingHiperwallEdits);
        Assert.Equal(unknown.Request.RequestId, receipt.Request.RequestId);
        Assert.False(receipt.Active); Assert.Equal(HiperwallSendState.Unknown, receipt.Steps.Single().State);
        // Read results are detached copies and cannot clear authoritative uncertainty.
        receipt.Steps.Single().State = HiperwallSendState.Acknowledged;
        Assert.Single(r.Service.GetState(r.Admin.Token).OutstandingHiperwallEdits);
        await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Equal(2, fixture.Commands.Count);
    }

    [Fact]
    public void Old_unknown_is_visible_beyond_100_recent_records_and_after_restart()
    {
        using var r = new Rig();
        HiperwallEditReceipt Receipt(int i, HiperwallSendState state) => new()
        {
            Request = new(Guid.NewGuid(), r.Generation, 1, HiperwallEditAction.Open),
            Requester = r.Admin.Session, Endpoint = "http://127.0.0.1:8000",
            AcceptedAt = r.Clock.GetUtcNow().AddSeconds(i),
            Steps = [new() { Command = new(HiperwallEditAction.Open, InstanceId: $"instance-{i}"), State = state }]
        };
        var persisted = r.Store.Load();
        var old = Receipt(0, HiperwallSendState.Unknown);
        persisted.HiperwallEdits.Add(old);
        for (var i = 1; i <= 101; i++) persisted.HiperwallEdits.Add(Receipt(i, HiperwallSendState.Acknowledged));
        r.Store.Save(persisted); r.Restart();
        var outstanding = Assert.Single(r.Service.GetState(r.Admin.Token).OutstandingHiperwallEdits);
        Assert.Equal(old.Request.RequestId, outstanding.Request.RequestId);
        var history = r.Service.GetHiperwallEdits(r.Admin.Token);
        Assert.Equal(101, history.Length);
        Assert.Single(history, x => x.Request.RequestId == old.Request.RequestId);
    }
}
