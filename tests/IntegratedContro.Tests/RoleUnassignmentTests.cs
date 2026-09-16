using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class RoleUnassignmentTests
{
    [Fact]
    public async Task Unassign_preserves_device_other_aliases_definitions_history_and_persisted_versions()
    {
        using var r = new Rig(); var device = r.Device(); var other = r.Device("other");
        r.Service.SaveRole(r.Admin.Token, new(r.Generation, "alias", device.Id));
        var completed = r.Service.Submit(r.Admin.Token, r.Manual());
        await r.Service.DispatchNextAsync();
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Brightness, 40));
        var before = r.Service.GetState(r.Admin.Token);
        Assert.True(before.RoleUnassignmentSupported);
        Assert.True(r.Service.UnassignRole(r.Admin.Token, new(r.Generation, " light ", device.Id, 1)));
        var after = r.Service.GetState(r.Admin.Token);
        Assert.Equal(before.Devices, after.Devices);
        Assert.Equal(before.DeviceStates[device.Id].Simulated, after.DeviceStates[device.Id].Simulated);
        Assert.Equal(before.Scenarios.Single().Steps, after.Scenarios.Single().Steps);
        Assert.Equal(before.Jobs.Single().Snapshot.Steps, after.Jobs.Single().Snapshot.Steps);
        Assert.Equal(JobStatus.Completed, r.Job(completed.Id).Status);
        Assert.DoesNotContain(after.Roles, role => role.Id == "light");
        Assert.Contains(after.Roles, role => role.Id == "alias" && role.DeviceId == device.Id);
        Assert.Contains(after.Roles, role => role.Id == "other" && role.DeviceId == other.Id);
        Rig.Reject("role_missing", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        Rig.Reject("role_missing", () => r.SubmitScenario(definition));
        var audit = Assert.Single(after.Audit, a => a.Action == "RoleUnassigned");
        Assert.Equal("역할 배정 해제", audit.EventName); Assert.Contains("light", audit.Message);
        Assert.Equal(r.Admin.Session.UserId, audit.UserId);

        r.Service.Release(r.Admin.Token, r.Generation); r.Restart();
        r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        Assert.DoesNotContain(r.Service.GetState(r.Admin.Token).Roles, role => role.Id == "light");
        Assert.Equal(audit, r.Service.GetState(r.Admin.Token).Audit.Single(a => a.Action == "RoleUnassigned"));
        var recreated = r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", device.Id));
        Assert.Equal(2, recreated.Version);
        Rig.Reject("version_conflict", () => r.Service.UnassignRole(r.Admin.Token, new(r.Generation, "light", device.Id, 1)));
        Rig.Reject("version_conflict", () => r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", other.Id, 1)));
        var next = r.SubmitScenario(definition); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(next.Id).Status);
        Assert.Equal(recreated, r.Job(next.Id).Snapshot.Steps[0].Role);
        r.Service.UnassignRole(r.Admin.Token, new(r.Generation, "light", device.Id, 2));
        Assert.Equal(3, r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", device.Id)).Version);
    }

    [Fact]
    public void Unassign_requires_current_admin_owner_and_exact_device_and_version()
    {
        using var r = new Rig(); var d = r.Device(); var op = r.Operator("operator"); var other = r.Device("other");
        var request = new UnassignRoleRequest(r.Generation, "light", d.Id, 1);
        Rig.Reject("unauthorized", () => r.Service.UnassignRole("missing-token", request));
        Rig.Reject("lease_required", () => r.Service.UnassignRole(r.Admin.Token, request with { Generation = r.Generation - 1 }));
        Rig.Reject("lease_required", () => r.Service.UnassignRole(op.Token, request));
        Rig.Reject("version_conflict", () => r.Service.UnassignRole(r.Admin.Token, request with { DeviceId = other.Id }));
        Rig.Reject("version_conflict", () => r.Service.UnassignRole(r.Admin.Token, request with { ExpectedVersion = 2 }));
        Rig.Reject("version_conflict", () => r.Service.UnassignRole(r.Admin.Token, request with { Id = "missing" }));
        Rig.Reject("invalid_role", () => r.Service.UnassignRole(r.Admin.Token, request with { ExpectedVersion = 0 }));
        Assert.Equal(2, r.Service.GetState(r.Admin.Token).Roles.Length);
        Assert.DoesNotContain(r.Service.GetState(r.Admin.Token).Audit, a => a.Action == "RoleUnassigned");
        r.Service.Release(r.Admin.Token, r.Generation); var lease = r.Service.Acquire(op.Token);
        Rig.Reject("admin_required", () => r.Service.UnassignRole(op.Token, request with { Generation = lease.Generation }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Queued_manual_or_future_scenario_role_blocks_unassignment_until_cancelled(bool scenario)
    {
        using var r = new Rig(); var d = r.Device(); r.Device("other");
        var job = scenario
            ? r.SubmitScenario(r.Scenario(new ScenarioStep("other", DeviceOperation.Power, 1, 60000),
                new("light", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue)))
            : r.Service.Submit(r.Admin.Token, r.Manual(delay: 60000));
        var request = new UnassignRoleRequest(r.Generation, "light", d.Id, 1);
        Rig.Reject("role_in_use", () => r.Service.UnassignRole(r.Admin.Token, request));
        Assert.True(r.Job(job.Id).Active); Assert.Empty(r.Driver.Sent);
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id));
        Assert.True(r.Service.UnassignRole(r.Admin.Token, request));
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status); Assert.Empty(r.Driver.Sent);
    }

    [Fact]
    public async Task Sending_and_stop_requested_work_keep_role_until_sent_result_is_recorded()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.Hold = true;
        var job = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1),
            new("light", DeviceOperation.Brightness, 30)));
        var dispatch = r.Service.DispatchNextAsync(); await r.Driver.Started.Task;
        var request = new UnassignRoleRequest(r.Generation, "light", d.Id, 1);
        Rig.Reject("role_in_use", () => r.Service.UnassignRole(r.Admin.Token, request));
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id));
        Rig.Reject("role_in_use", () => r.Service.UnassignRole(r.Admin.Token, request));
        r.Driver.Completion.SetResult(new(StepStatus.Simulated, "Test completed"));
        await dispatch;
        Assert.True(r.Service.UnassignRole(r.Admin.Token, request));
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Simulated, r.Job(job.Id).Steps[0].Status);
        Assert.Single(r.Driver.Sent); Assert.False(await r.Service.DispatchNextAsync());
    }

    [Fact]
    public async Task Recreated_binding_cannot_accept_stale_card_expectation()
    {
        using var r = new Rig(); var d = r.Device();
        var state = await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        var power = state.Simulated[DeviceOperation.Power];
        var request = r.Manual() with { Value = 0, CardPower = new(d.Id, d.PcId, d.Version, 1, power.Value, power.At) };
        r.Service.UnassignRole(r.Admin.Token, new(r.Generation, "light", d.Id, 1));
        var replacement = r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", d.Id));
        Rig.Reject("card_target_changed", () => r.Service.Submit(r.Admin.Token, request));
        Assert.Empty(r.Service.GetState(r.Admin.Token).Jobs);
        r.Service.Submit(r.Admin.Token, request with { CardPower = request.CardPower! with { RoleVersion = replacement.Version } });
        await r.Service.DispatchNextAsync(); Assert.Single(r.Driver.Sent);
    }

    [Fact]
    public async Task Concurrent_submit_and_unassign_cannot_leave_accepted_work_with_missing_role()
    {
        using var r = new Rig(); var d = r.Device();
        var admitted = false; var removed = false;
        var submit = Task.Run(() =>
        {
            try { r.Service.Submit(r.Admin.Token, r.Manual(delay: 60000)); admitted = true; }
            catch (DomainException error) { Assert.Equal("role_missing", error.Code); }
        });
        var unassign = Task.Run(() =>
        {
            try { r.Service.UnassignRole(r.Admin.Token, new(r.Generation, "light", d.Id, 1)); removed = true; }
            catch (DomainException error) { Assert.Equal("role_in_use", error.Code); }
        });
        await Task.WhenAll(submit, unassign);
        Assert.NotEqual(admitted, removed);
        Assert.Equal(admitted, r.Service.GetState(r.Admin.Token).Roles.Any(role => role.Id == "light"));
        Assert.Equal(admitted ? 1 : 0, r.Service.GetState(r.Admin.Token).Jobs.Length);
    }
}
