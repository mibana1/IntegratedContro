using System.Text.Json;
using IntegratedContro.App;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class LightSlotTests
{
    private static SaveLightSlotRequest Capture(Rig r, int number = 1, int version = 0)
    {
        var s = r.Service.GetState(r.Admin.Token);
        return new(r.Generation, number, version, "  운영 배치  ", s.LightLayout.Version, s.LightLayout.DeviceIds.Select(id =>
        {
            var d = s.Devices.Single(d => d.Id == id); var role = s.Roles.First(x => x.DeviceId == id);
            var power = s.DeviceStates[id].Values[DeviceOperation.Power];
            return new LightPowerTarget(role.Id, new(id, d.PcId, d.Version, role.Version, power.Value, power.At));
        }).ToArray());
    }
    private static async Task<LightSlot> Save(Rig r)
    {
        var ids = r.Service.GetState(r.Admin.Token).LightLayout.DeviceIds;
        for (var i = 0; i < ids.Length; i++)
        {
            r.Driver.ReadPower = i % 2;
            await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, ids[i]));
        }
        return r.Service.SaveLightSlot(r.Admin.Token, Capture(r));
    }
    private static RestoreLightSlotRequest Restore(Rig r, LightSlot slot) =>
        new(Guid.NewGuid(), r.Generation, slot.Number, slot.Version, r.Service.GetState(r.Admin.Token).LightLayout.Version);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    [Fact]
    public async Task Save_is_durable_without_control_and_restore_freezes_mixed_power_and_layout_atomically()
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        var group = new LightGroup(Guid.NewGuid(), "무대", [b.Id, a.Id]);
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id], [group]));
        var slot = await Save(r);
        Assert.Equal("운영 배치", slot.Name); Assert.Empty(r.Driver.Sent);
        Assert.Empty(r.Service.GetState(r.Admin.Token).Jobs);
        Assert.Equal(new[] { 0, 1 }, slot.PowerStates.Select(s => s.Value));
        r.Service.Release(r.Admin.Token, r.Generation); r.Restart();
        Assert.Equal(Json(slot), Json(Assert.Single(r.Service.GetState(r.Admin.Token).LightSlots)));
        r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 1, [a.Id, b.Id], []));
        // Display names can change without invalidating execution identity.
        r.Service.SaveDevice(r.Admin.Token, new(r.Generation, a.Id, a.PcId, "renamed PC", "renamed light",
            a.ConnectionId, a.ModelId, LatencyMs: a.LatencyMs, ExpectedVersion: a.Version));
        var request = Restore(r, slot);
        var jobs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => r.Service.RestoreLightSlot(r.Admin.Token, request))));
        Assert.Single(jobs.Select(j => j.Id).Distinct());
        var job = jobs[0]; var snapshot = Json(job.Snapshot);
        var state = r.Service.GetState(r.Admin.Token);
        Assert.Equal(3, state.LightLayout.Version); Assert.Equal(Json(group), Json(Assert.Single(state.LightLayout.Groups)));
        Assert.Equal(new[] { b.Id, a.Id }, state.LightLayout.DeviceIds);
        Assert.Equal(JobKind.LightSlot, job.Kind); Assert.True(job.IsDeviceBatch);
        Rig.Reject("lighting_batch_busy", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        Rig.Reject("request_id_conflict", () => r.Service.RestoreLightSlot(r.Admin.Token, request with { Number = 2 }));
        Rig.Reject("request_id_conflict", () => r.Service.Submit(r.Admin.Token, r.Manual() with { RequestId = request.RequestId }));
        r.Service.DeleteLightSlot(r.Admin.Token, new(r.Generation, slot.Number, slot.Version));
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 3, [a.Id, b.Id], []));
        Assert.Equal(snapshot, Json(r.Job(job.Id).Snapshot));
        r.Service.Release(r.Admin.Token, r.Generation);
        Assert.Equal(job.Id, r.Service.RestoreLightSlot(r.Admin.Token, request).Id);
        await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status);
        Assert.Equal(new[] { 0, 1 }, r.Driver.Sent.Select(s => s.Value));
        Assert.Equal(new[] { b.Id, a.Id }, r.Driver.Sent.Select(s => s.Target.Id));
    }

    [Fact]
    public async Task Save_rejects_missing_changed_or_busy_observations_and_stale_slot_versions()
    {
        using var r = new Rig(); var a = r.Device(); r.Device("b");
        var slot = await Save(r); var request = Capture(r, version: slot.Version);
        var before = Json(r.Service.GetState(r.Admin.Token).LightSlots);
        Rig.Reject("invalid_slot", () => r.Service.SaveLightSlot(r.Admin.Token, request with { Number = 7 }));
        Rig.Reject("invalid_input", () => r.Service.SaveLightSlot(r.Admin.Token, request with { Name = " " }));
        Rig.Reject("light_list_changed", () => r.Service.SaveLightSlot(r.Admin.Token, request with { Targets = [request.Targets[0]] }));
        Rig.Reject("light_state_changed", () => r.Service.SaveLightSlot(r.Admin.Token, request with
        { Targets = [request.Targets[0] with { Expected = request.Targets[0].Expected with { ObservedAt = DateTimeOffset.MinValue } }, request.Targets[1]] }));
        Rig.Reject("card_target_changed", () => r.Service.SaveLightSlot(r.Admin.Token, request with
        { Targets = [request.Targets[0] with { Expected = request.Targets[0].Expected with { PcId = Guid.NewGuid() } }, request.Targets[1]] }));
        var job = r.Service.Submit(r.Admin.Token, r.Manual(delay: 5000));
        Rig.Reject("device_busy", () => r.Service.SaveLightSlot(r.Admin.Token, request));
        Assert.Equal(before, Json(r.Service.GetState(r.Admin.Token).LightSlots)); Assert.Empty(r.Driver.Sent);
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id));
        var cleared = r.Service.DeleteLightSlot(r.Admin.Token, new(r.Generation, 1, slot.Version));
        Assert.True(cleared.IsEmpty);
        Rig.Reject("version_conflict", () => r.Service.SaveLightSlot(r.Admin.Token, request));
        Rig.Reject("version_conflict", () => r.Service.DeleteLightSlot(r.Admin.Token, new(r.Generation, 1, slot.Version)));
        r.Service.Release(r.Admin.Token, r.Generation); r.Restart();
        Assert.Equal(cleared.Version, Assert.Single(r.Service.GetState(r.Admin.Token).LightSlots).Version);
        Assert.True(r.Service.GetState(r.Admin.Token).LightSlots.Single().IsEmpty);
    }

    [Theory]
    [InlineData("device-list", "light_list_changed")]
    [InlineData("role", "slot_target_changed")]
    [InlineData("configuration", "slot_target_changed")]
    [InlineData("layout", "layout_changed")]
    [InlineData("busy", "device_busy")]
    [InlineData("uncertain", "device_uncertain")]
    public async Task Restore_failure_leaves_current_layout_and_job_set_unchanged(string change, string code)
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b");
        var slot = await Save(r);
        r.Service.SaveLightOrder(r.Admin.Token, new(r.Generation, 0, [b.Id, a.Id], [new(Guid.NewGuid(), "현재 그룹", [b.Id])]));
        var request = Restore(r, slot);
        switch (change)
        {
            case "device-list": r.Device("new"); break;
            case "role": r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", b.Id, 1)); break;
            case "configuration":
                r.Service.SaveDevice(r.Admin.Token, new(r.Generation, a.Id, Guid.NewGuid(), "new PC", a.Name,
                    a.ConnectionId, a.ModelId, LatencyMs: a.LatencyMs, ExpectedVersion: a.Version)); break;
            case "layout": request = request with { LayoutVersion = 0 }; break;
            case "busy": r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 5000))); break;
            case "uncertain":
                var job = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 5000)));
                r.Service.BeginManualSwitch(r.Admin.Token, new(r.Generation, job.Id)); break;
        }
        var before = r.Service.GetState(r.Admin.Token);
        Rig.Reject(code, () => r.Service.RestoreLightSlot(r.Admin.Token, request));
        var after = r.Service.GetState(r.Admin.Token);
        Assert.Equal(Json(before.LightLayout), Json(after.LightLayout));
        Assert.Equal(Json(before.Jobs), Json(after.Jobs)); Assert.Empty(r.Driver.Sent);
    }

    [Fact]
    public async Task All_slot_mutations_require_current_admin_owner()
    {
        using var r = new Rig(); r.Device(); var slot = await Save(r);
        var save = Capture(r, version: slot.Version); var restore = Restore(r, slot);
        var op = r.Operator("operator");
        Rig.Reject("unauthorized", () => r.Service.RestoreLightSlot("missing", restore));
        Rig.Reject("lease_required", () => r.Service.RestoreLightSlot(r.Admin.Token, restore with { Generation = r.Generation - 1 }));
        r.Service.Release(r.Admin.Token, r.Generation);
        Rig.Reject("lease_required", () => r.Service.SaveLightSlot(r.Admin.Token, save));
        Rig.Reject("lease_required", () => r.Service.DeleteLightSlot(r.Admin.Token, new(r.Generation, 1, slot.Version)));
        var generation = r.Service.Acquire(op.Token).Generation;
        Rig.Reject("admin_required", () => r.Service.SaveLightSlot(op.Token, save with { Generation = generation }));
        Rig.Reject("admin_required", () => r.Service.DeleteLightSlot(op.Token, new(generation, 1, slot.Version)));
        Rig.Reject("admin_required", () => r.Service.RestoreLightSlot(op.Token, restore with { Generation = generation }));
        Assert.Empty(r.Service.GetState(op.Token).Jobs);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("handoff-cancel")]
    [InlineData("failure")]
    [InlineData("configuration")]
    public async Task Remaining_power_steps_stop_without_replay_or_rollback(string change)
    {
        using var r = new Rig(); var a = r.Device(); var b = r.Device("b"); var op = r.Operator("next");
        var slot = await Save(r); var job = r.Service.RestoreLightSlot(r.Admin.Token, Restore(r, slot));
        if (change == "failure") r.Driver.NextStatus = DriverStatus.Unknown;
        await r.Service.DispatchNextAsync();
        switch (change)
        {
            case "restart": r.Restart(); break;
            case "handoff-cancel":
                r.Service.Release(r.Admin.Token, r.Generation); var generation = r.Service.Acquire(op.Token).Generation;
                r.Service.Cancel(op.Token, new(generation, job.Id)); break;
            case "configuration":
                r.Service.SaveDevice(r.Admin.Token, new(r.Generation, b.Id, Guid.NewGuid(), "other", b.Name,
                    b.ConnectionId, b.ModelId, LatencyMs: b.LatencyMs, ExpectedVersion: b.Version)); break;
        }
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent); Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[1].Status);
        Assert.Equal("admin", r.Job(job.Id).Snapshot.RequesterName);
        if (change == "handoff-cancel") Assert.Equal("next", r.Job(job.Id).CancellerName);
        if (change == "failure") { r.Restart(); await r.Service.DispatchNextAsync(); Assert.Single(r.Driver.Sent); }
        Assert.Equal(slot.Layout.DeviceIds, r.Service.GetState(r.Admin.Token).LightLayout.DeviceIds);
    }

    [Fact]
    public void Slot_editor_keeps_selection_and_failed_draft_and_resets_between_sessions()
    {
        var host = new FeatureHostFake(); var vm = host.Lighting();
        Assert.False(vm.SaveLightSlotCommand.CanExecute(null));
        host.Publish(host.State with { LightSlotsSupported = true });
        vm.SelectedLightSlot = vm.LightSlots[2]; vm.LightSlotName = "야간";
        host.Publish(JsonDefaults.Copy(host.State));
        Assert.Equal(3, vm.SelectedLightSlot.Number); Assert.Equal("야간", vm.LightSlotName);
        host.FailSave = true; vm.SaveLightSlotCommand.Execute(null);
        Assert.NotNull(host.Error); Assert.Equal("야간", vm.LightSlotName); Assert.False(vm.SelectedLightSlot.HasSaved);
        host.FailSave = false; host.Execute(vm.SaveLightSlotCommand);
        Assert.True(vm.SelectedLightSlot.HasSaved);
        host.Execute(vm.RestoreLightSlotCommand);
        Assert.Equal(3, Assert.Single(host.SlotRestores).Number);
        host.Context = host.Context with { HasPending = true }; host.Publish(host.State);
        Assert.False(vm.RestoreLightSlotCommand.CanExecute(null)); Assert.False(vm.SaveLightSlotCommand.CanExecute(null));
        host.Context = host.Context with { HasPending = false, CanConfigure = false }; host.Publish(host.State);
        Assert.False(vm.DeleteLightSlotCommand.CanExecute(null));
        host.Context = host.Context with { CanConfigure = true }; host.Publish(host.State);
        vm.LightSlotName = "private";
        host.Publish(host.State with { Session = host.State.Session with { Id = Guid.NewGuid() }, LightSlots = [] });
        Assert.Equal(1, vm.SelectedLightSlot.Number); Assert.Equal("슬롯 1", vm.LightSlotName);
    }
}
