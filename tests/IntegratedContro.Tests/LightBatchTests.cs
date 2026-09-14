using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class LightBatchTests
{
    private static async Task<LightBatchRequest> Request(Rig r, Guid? group = null, int value = 0)
    {
        var state = r.Service.GetState(r.Admin.Token);
        var ids = group is null ? state.LightLayout.DeviceIds : state.LightLayout.Groups.Single(g => g.Id == group).DeviceIds;
        foreach (var id in ids) await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, id));
        state = r.Service.GetState(r.Admin.Token);
        return new(Guid.NewGuid(), r.Generation, value, state.LightLayout.Version, group, ids.Select(id =>
        {
            var d = state.Devices.Single(d => d.Id == id); var role = state.Roles.Single(x => x.DeviceId == id);
            var power = state.DeviceStates[id].Simulated[DeviceOperation.Power];
            return new LightPowerTarget(role.Id, new(d.Id, d.PcId, d.Version, role.Version, power.Value, power.At));
        }).ToArray());
    }
    [Fact]
    public async Task Group_snapshot_deduplicates_and_survives_relayout_and_release()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b"); var outside = r.Device("outside");
        var id = Guid.NewGuid();
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id, outside.Id], [new(id, "무대", [b.Id, a.Id])]));
        var request = await Request(r, id);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => r.Service.SubmitLightBatch(r.Admin.Token, request))));
        Assert.Single(accepted.Select(j => j.Id).Distinct());
        var job = accepted[0]; var snapshot = JsonSerializer.Serialize(job.Snapshot);
        Assert.Equal(new[] { b.Id, a.Id }, job.Snapshot.Steps.Select(s => s.Target!.Id));
        Assert.All(job.Snapshot.Steps, s => { Assert.Equal(0, s.Value); Assert.Equal(1, s.ConditionValue); });
        Rig.Reject("lighting_batch_busy", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 1, [outside.Id, a.Id, b.Id], [new(id, "변경", [outside.Id])]));
        Assert.Equal(snapshot, JsonSerializer.Serialize(r.Job(job.Id).Snapshot));
        r.Service.Release(r.Admin.Token, r.Generation);
        Assert.Equal(job.Id, r.Service.SubmitLightBatch(r.Admin.Token, request).Id);
        Rig.Reject("lease_required", () => r.Service.SubmitLightBatch(r.Admin.Token, request with { RequestId = Guid.NewGuid() }));
        await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status);
        Assert.DoesNotContain(r.Driver.Sent, s => s.Target!.Id == outside.Id);
    }
    [Fact]
    public async Task Entire_set_is_rejected_for_stale_target_membership_or_busy_device()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        var request = await Request(r);
        Rig.Reject("light_list_changed", () => r.Service.SubmitLightBatch(r.Admin.Token, request with { Targets = [request.Targets[0]] }));
        Rig.Reject("light_list_changed", () => r.Service.SubmitLightBatch(r.Admin.Token, request with { Targets = [request.Targets[0], request.Targets[0]] }));
        Rig.Reject("card_target_changed", () => r.Service.SubmitLightBatch(r.Admin.Token, request with
        { Targets = [request.Targets[0], request.Targets[1] with { Expected = request.Targets[1].Expected with { PcId = a.PcId } }] }));
        Rig.Reject("light_state_changed", () => r.Service.SubmitLightBatch(r.Admin.Token, request with
        { Targets = [request.Targets[0], request.Targets[1] with { Expected = request.Targets[1].Expected with { ObservedAt = DateTimeOffset.MinValue } }] }));
        Assert.Empty(r.Service.GetState(r.Admin.Token).Jobs);
        var scenario = r.SubmitScenario(r.Scenario(new ScenarioStep("b", DeviceOperation.Power, 1, 60000)));
        Rig.Reject("device_busy", () => r.Service.SubmitLightBatch(r.Admin.Token, request));
        Assert.Single(r.Service.GetState(r.Admin.Token).Jobs);
        r.Service.BeginManualSwitch(r.Admin.Token, new(r.Generation, scenario.Id));
        Rig.Reject("device_uncertain", () => r.Service.SubmitLightBatch(r.Admin.Token, request));
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id]));
        Rig.Reject("layout_changed", () => r.Service.SubmitLightBatch(r.Admin.Token, request));
        Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Scope_is_checked_before_acceptance_and_again_before_each_dispatch()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        var request = await Request(r); var op = r.Operator("limited", all: false, ids: [a.Id]);
        r.Service.Release(r.Admin.Token, r.Generation); var gen = r.Service.Acquire(op.Token).Generation;
        Rig.Reject("target_forbidden", () => r.Service.SubmitLightBatch(op.Token, request with { Generation = gen }));
        Assert.Empty(r.Service.GetState(op.Token).Jobs);
        r.Service.Release(op.Token, gen); r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        r.Service.UpdateAccount(r.Admin.Token, new(r.Generation, op.Session.UserId, true, true, []));
        r.Service.Release(r.Admin.Token, r.Generation); gen = r.Service.Acquire(op.Token).Generation;
        var accepted = r.Service.SubmitLightBatch(op.Token, request with { Generation = gen });
        await r.Service.DispatchNextAsync();
        r.Service.Release(op.Token, gen); r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        r.Service.UpdateAccount(r.Admin.Token, new(r.Generation, op.Session.UserId, true, false, [a.Id]));
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent); Assert.Equal(StepStatus.Skipped, r.Job(accepted.Id).Steps[1].Status);
        Assert.Equal(StepStatus.Simulated, r.Job(accepted.Id).Steps[0].Status);
    }
    [Fact]
    public async Task Handoff_can_cancel_remaining_steps_without_rolling_back_sent_results()
    {
        using var r = new Rig(); r.Device(); r.Device("b");
        var op = r.Operator("next"); var job = r.Service.SubmitLightBatch(r.Admin.Token, await Request(r));
        await r.Service.DispatchNextAsync();
        r.Service.Release(r.Admin.Token, r.Generation); var gen = r.Service.Acquire(op.Token).Generation;
        r.Service.Cancel(op.Token, new(gen, job.Id)); await r.Service.DispatchNextAsync();
        var after = r.Job(job.Id);
        Assert.Single(r.Driver.Sent); Assert.Equal(StepStatus.Simulated, after.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, after.Steps[1].Status);
        Assert.Equal("admin", after.Snapshot.RequesterName); Assert.Equal("next", after.CancellerName);
    }
    [Theory]
    [InlineData(StepStatus.Unknown)]
    [InlineData(StepStatus.Failed)]
    public async Task Failure_or_uncertainty_stops_remaining_steps_and_never_replays(StepStatus status)
    {
        using var r = new Rig(); r.Device(); r.Device("b");
        var job = r.Service.SubmitLightBatch(r.Admin.Token, await Request(r));
        r.Driver.NextStatus = status; await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[1].Status);
        r.Restart(); await r.Service.DispatchNextAsync(); Assert.Single(r.Driver.Sent);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_recovers_untouched_work_but_does_not_resume_started_batch(bool started)
    {
        using var r = new Rig(); r.Device(); r.Device("b");
        var request = await Request(r); var job = r.Service.SubmitLightBatch(r.Admin.Token, request);
        if (started) await r.Service.DispatchNextAsync();
        r.Restart(); await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Equal(started ? 1 : 2, r.Driver.Sent.Count);
        Assert.Equal(started ? JobStatus.Interrupted : JobStatus.Completed, r.Job(job.Id).Status);
        Assert.Equal(request.RequestId, r.Job(job.Id).Snapshot.RequestId);
    }
    [Fact]
    public async Task Absolute_on_does_not_toggle_and_changed_or_cross_route_request_ids_are_rejected()
    {
        using var r = new Rig(); r.Device(); r.Device("b");
        var request = await Request(r, value: 1); var job = r.Service.SubmitLightBatch(r.Admin.Token, request);
        Rig.Reject("request_id_conflict", () => r.Service.SubmitLightBatch(r.Admin.Token, request with { Value = 0 }));
        Rig.Reject("request_id_conflict", () => r.Service.Submit(r.Admin.Token, r.Manual() with { RequestId = request.RequestId }));
        await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.All(r.Driver.Sent, s => Assert.Equal(1, s.Value));
        var next = await Request(r, value: 0); r.Clock.Advance(16); r.Service.CheckConnections();
        Rig.Reject("session_fenced", () => r.Service.SubmitLightBatch(r.Admin.Token, next));
        Assert.Equal(job.Id, r.Service.SubmitLightBatch(r.Admin.Token, request).Id);
    }
    private enum LegacyKind { Manual, Scenario }
    private sealed record LegacyJob(LegacyKind Kind);
    [Fact]
    public async Task Legacy_host_cannot_silently_read_a_batch_as_an_unrestricted_manual_job()
    {
        using var r = new Rig(); r.Device();
        var job = r.Service.SubmitLightBatch(r.Admin.Token, await Request(r));
        var json = JsonSerializer.Serialize(job, JsonDefaults.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LegacyJob>(json, JsonDefaults.Options));
        Assert.Equal(JobKind.LightBatch, JsonSerializer.Deserialize<Job>(json, JsonDefaults.Options)!.Kind);
    }
    [Fact]
    public async Task Fresh_state_condition_is_checked_before_physical_dispatch()
    {
        using var r = new Rig(); r.Device(); r.Device("b");
        var job = r.Service.SubmitLightBatch(r.Admin.Token, await Request(r));
        r.Driver.ReadPower = 0; await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent); Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
    }
}
