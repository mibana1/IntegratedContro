using IntegratedContro.Core;
namespace IntegratedContro.Tests;

public sealed class LightingTests
{
    private static SubmitRequest Card(Rig r, DeviceConfig d, string role = "light")
    {
        var s = r.Service.GetState(r.Admin.Token);
        var power = s.DeviceStates[d.Id].Simulated[DeviceOperation.Power];
        return new(Guid.NewGuid(), r.Generation, role, Value: 1 - power.Value, ExpiresAfterSeconds: 30,
            CardPower: new(d.Id, d.PcId, d.Version, s.Roles.Single(x => x.Id == role).Version, power.Value, power.At));
    }
    [Fact]
    public async Task Order_is_durable_shared_and_does_not_change_accepted_targets_or_config_versions()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("other");
        var queued = r.Service.Submit(r.Admin.Token, r.Manual());
        var before = JsonDefaults.Copy(queued.Snapshot);
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id]));
        var observer = r.Login();
        Assert.Equal(new[] { b.Id, a.Id }, r.Service.GetState(observer.Token).LightLayout.DeviceIds);
        Assert.Equal(a.Version, r.Service.GetState(r.Admin.Token).Devices.Single(x => x.Id == a.Id).Version);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(r.Job(queued.Id).Snapshot));
        r.Restart();
        Assert.Equal(new[] { b.Id, a.Id }, r.Service.GetState(r.Admin.Token).LightLayout.DeviceIds);
        Assert.Equal(1, r.Service.GetState(r.Admin.Token).LightLayout.Version);
        await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(queued.Id).Status);
    }
    [Fact]
    public async Task Concurrent_order_edits_allow_only_one_version_and_reject_stale_or_invalid_lists()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            try { r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id])); return true; }
            catch (DomainException e) { Assert.Equal("layout_changed", e.Code); return false; }
        })));
        Assert.Single(results, x => x);
        foreach (var ids in new[] { new[] { a.Id }, new[] { a.Id, a.Id }, new[] { a.Id, Guid.NewGuid() } })
            Rig.Reject("light_list_changed", () => r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 1, ids)));
        var op = r.Operator("operator"); r.Service.Release(r.Admin.Token, r.Generation);
        Rig.Reject("lease_required", () => r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 1, [a.Id, b.Id])));
        var generation = r.Service.Acquire(op.Token).Generation;
        Rig.Reject("admin_required", () => r.Service.SaveLightOrder(op.Token, new(generation, 1, [a.Id, b.Id])));
    }
    [Fact]
    public async Task Power_card_deduplicates_and_blocks_a_second_pending_toggle_and_late_new_requests()
    {
        using var r = new Rig(); var d = r.Device();
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        var request = Card(r, d);
        var jobs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => r.Service.Submit(r.Admin.Token, request))));
        Assert.Single(jobs.Select(j => j.Id).Distinct());
        Rig.Reject("device_busy", () => r.Service.Submit(r.Admin.Token, request with { RequestId = Guid.NewGuid() }));
        Assert.Equal(DeviceOperation.Power, jobs[0].Snapshot.Steps[0].ConditionOperation);
        Assert.Equal(0, jobs[0].Snapshot.Steps[0].Value);
        r.Service.Release(r.Admin.Token, r.Generation);
        Assert.Equal(jobs[0].Id, r.Service.Submit(r.Admin.Token, request).Id);
        Rig.Reject("lease_required", () => r.Service.Submit(r.Admin.Token, request with { RequestId = Guid.NewGuid() }));
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent);
        Assert.Equal(0, r.Driver.Sent[0].Value);
    }
    [Fact]
    public async Task Card_rejects_stale_pc_device_role_and_state_without_redirecting()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, a.Id));
        var request = Card(r, a);
        foreach (var expected in new[] { request.CardPower! with { PcId = b.PcId },
            request.CardPower! with { DeviceId = b.Id }, request.CardPower! with { DeviceVersion = a.Version + 1 },
            request.CardPower! with { RoleVersion = 999 } })
            Rig.Reject("card_target_changed", () => r.Service.Submit(r.Admin.Token, request with { CardPower = expected }));
        Rig.Reject("light_state_changed", () => r.Service.Submit(r.Admin.Token, request with { Value = 1 }));
        Rig.Reject("light_state_changed", () => r.Service.Submit(r.Admin.Token, request with { CardPower = request.CardPower! with { ObservedAt = DateTimeOffset.MinValue } }));
        r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", b.Id, 1));
        Rig.Reject("card_target_changed", () => r.Service.Submit(r.Admin.Token, request));
        Assert.Empty(r.Service.GetState(r.Admin.Token).Jobs);
    }
    [Fact]
    public async Task Card_respects_reservations_uncertainty_permissions_and_fresh_pre_send_condition()
    {
        using var r = new Rig(); var d = r.Device(); var other = r.Device("other");
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        var request = Card(r, d);
        var scenario = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 60000)));
        Rig.Reject("device_busy", () => r.Service.Submit(r.Admin.Token, request));
        r.Service.BeginManualSwitch(r.Admin.Token, new(r.Generation, scenario.Id));
        Rig.Reject("device_uncertain", () => r.Service.Submit(r.Admin.Token, request));
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        var job = r.Service.Submit(r.Admin.Token, Card(r, d));
        r.Driver.ReadPower = 0;
        await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent);
        Assert.NotEqual(JobStatus.Completed, r.Job(job.Id).Status);
        var op = r.Operator("limited", all: false, ids: [other.Id]);
        r.Service.Release(r.Admin.Token, r.Generation);
        var generation = r.Service.Acquire(op.Token).Generation;
        Rig.Reject("target_forbidden", () => r.Service.Submit(op.Token, request with { Generation = generation, RequestId = Guid.NewGuid() }));
    }
    [Fact]
    public void Groups_are_shared_durable_and_legacy_order_edits_preserve_membership()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        var groupId = Guid.NewGuid();
        var accepted = r.Service.Submit(r.Admin.Token, r.Manual());
        var before = System.Text.Json.JsonSerializer.Serialize(accepted.Snapshot);
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id], [new(groupId, " 무대 ", [b.Id])]));
        var observer = r.Login();
        var group = Assert.Single(r.Service.GetState(observer.Token).LightLayout.Groups);
        Assert.Equal("무대", group.Name); Assert.Equal(new[] { b.Id }, group.DeviceIds);
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 1, [a.Id, b.Id]));
        Assert.Equal(groupId, Assert.Single(r.Service.GetState(r.Admin.Token).LightLayout.Groups).Id);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(r.Job(accepted.Id).Snapshot));
        r.Restart();
        Assert.Equal(groupId, Assert.Single(r.Service.GetState(r.Admin.Token).LightLayout.Groups).Id);
        Assert.Equal(2, r.Service.GetState(r.Admin.Token).LightLayout.Version);
    }
    [Fact]
    public void Invalid_groups_are_rejected_atomically_and_deleting_a_group_keeps_devices()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b"); var id = Guid.NewGuid();
        var request = new LightOrderRequest(r.Generation, 0, [a.Id, b.Id]);
        Rig.Reject("duplicate_light_group", () => r.Service.SaveLightOrder(r.Admin.Token, request with { Groups = [new(id, "A", []), new(id, "B", [])] }));
        Rig.Reject("duplicate_light_group", () => r.Service.SaveLightOrder(r.Admin.Token, request with { Groups = [new(id, "A", []), new(Guid.NewGuid(), " a ", [])] }));
        Rig.Reject("invalid_group_members", () => r.Service.SaveLightOrder(r.Admin.Token, request with { Groups = [new(id, "A", [a.Id]), new(Guid.NewGuid(), "B", [a.Id])] }));
        Rig.Reject("invalid_group_members", () => r.Service.SaveLightOrder(r.Admin.Token, request with { Groups = [new(id, "A", [Guid.NewGuid()])] }));
        Rig.Reject("invalid_light_groups", () => r.Service.SaveLightOrder(r.Admin.Token, request with { Groups = [new(id, " ", [])] }));
        Assert.Equal(0, r.Service.GetState(r.Admin.Token).LightLayout.Version);
        r.Service.SaveLightOrder(r.Admin.Token, request with { Groups = [new(id, "A", [a.Id])] });
        r.Service.SaveLightOrder(r.Admin.Token, request with { ExpectedVersion = 1, Groups = [] });
        var state = r.Service.GetState(r.Admin.Token);
        Assert.Empty(state.LightLayout.Groups); Assert.Equal(2, state.Devices.Length);
        Assert.Equal(a.Version, state.Devices.Single(d => d.Id == a.Id).Version);
    }
    [Fact]
    public void Older_layout_json_without_groups_is_read_without_resetting_its_order()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"version":4,"deviceIds":["{{id}}"]}""";
        var layout = System.Text.Json.JsonSerializer.Deserialize<LightLayout>(json, JsonDefaults.Options)!;
        Assert.Equal(4, layout.Version); Assert.Equal(new[] { id }, layout.DeviceIds); Assert.Empty(layout.Groups);
    }
}
