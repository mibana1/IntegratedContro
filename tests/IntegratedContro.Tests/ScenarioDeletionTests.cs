using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class ScenarioDeletionTests
{
    [Fact]
    public async Task Delete_preserves_completed_history_and_survives_restart_without_reusing_versions()
    {
        using var r = new Rig(); r.Device();
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1));
        var other = r.Scenario(new ScenarioStep("light", DeviceOperation.Brightness, 25));
        var job = r.SubmitScenario(definition); await r.Service.DispatchNextAsync();
        var before = JsonDefaults.Copy(r.Job(job.Id));
        Assert.True(r.Service.GetState(r.Admin.Token).ScenarioDeletionSupported);
        Assert.True(r.Service.DeleteScenario(r.Admin.Token, new(r.Generation, definition.Id, definition.Version)));
        var state = r.Service.GetState(r.Admin.Token);
        Assert.Equal(other.Id, Assert.Single(state.Scenarios).Id);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before, JsonDefaults.Options),
            System.Text.Json.JsonSerializer.Serialize(r.Job(job.Id), JsonDefaults.Options));
        var audit = Assert.Single(state.Audit, a => a.Action == "ScenarioDeleted");
        Assert.Equal("시나리오 삭제", audit.EventName); Assert.Contains(definition.Name, audit.Message);
        Assert.Equal(r.Admin.Session.UserId, audit.UserId);
        Rig.Reject("scenario_missing", () => r.SubmitScenario(definition));
        Rig.Reject("version_conflict", () => r.Service.SaveScenario(r.Admin.Token,
            new(r.Generation, definition.Id, definition.Name, definition.Steps, definition.Version)));
        r.Service.Release(r.Admin.Token, r.Generation); r.Restart();
        r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        Assert.DoesNotContain(r.Service.GetState(r.Admin.Token).Scenarios, s => s.Id == definition.Id);
        Assert.Equal(audit, r.Service.GetState(r.Admin.Token).Audit.Single(a => a.Action == "ScenarioDeleted"));
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status);
        var recreated = r.Service.SaveScenario(r.Admin.Token, new(r.Generation, definition.Id, definition.Name, definition.Steps));
        Assert.Equal(definition.Version + 1, recreated.Version);
        Rig.Reject("version_conflict", () => r.Service.DeleteScenario(r.Admin.Token, new(r.Generation, definition.Id, definition.Version)));
        Rig.Reject("version_conflict", () => r.Service.SaveScenario(r.Admin.Token,
            new(r.Generation, definition.Id, definition.Name, definition.Steps, definition.Version)));
    }

    [Fact]
    public void Delete_requires_current_admin_owner_and_exact_version()
    {
        using var r = new Rig(); r.Device(); var op = r.Operator("operator");
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1));
        var request = new DeleteScenarioRequest(r.Generation, definition.Id, definition.Version);
        Rig.Reject("unauthorized", () => r.Service.DeleteScenario("missing", request));
        Rig.Reject("lease_required", () => r.Service.DeleteScenario(r.Admin.Token, request with { Generation = r.Generation - 1 }));
        Rig.Reject("lease_required", () => r.Service.DeleteScenario(op.Token, request));
        Rig.Reject("invalid_scenario", () => r.Service.DeleteScenario(r.Admin.Token, request with { Id = Guid.Empty }));
        Rig.Reject("invalid_scenario", () => r.Service.DeleteScenario(r.Admin.Token, request with { ExpectedVersion = 0 }));
        Rig.Reject("version_conflict", () => r.Service.DeleteScenario(r.Admin.Token, request with { ExpectedVersion = 2 }));
        Rig.Reject("version_conflict", () => r.Service.DeleteScenario(r.Admin.Token, request with { Id = Guid.NewGuid() }));
        Assert.Single(r.Service.GetState(r.Admin.Token).Scenarios);
        Assert.DoesNotContain(r.Service.GetState(r.Admin.Token).Audit, a => a.Action == "ScenarioDeleted");
        r.Service.Release(r.Admin.Token, r.Generation); var lease = r.Service.Acquire(op.Token);
        Rig.Reject("admin_required", () => r.Service.DeleteScenario(op.Token, request with { Generation = lease.Generation }));
    }

    [Fact]
    public void Queued_scenario_blocks_deletion_but_other_definitions_and_manual_work_do_not()
    {
        using var r = new Rig(); r.Device();
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 60000));
        var other = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 0));
        var job = r.SubmitScenario(definition);
        Rig.Reject("scenario_in_use", () => r.Service.DeleteScenario(r.Admin.Token, new(r.Generation, definition.Id, definition.Version)));
        Assert.True(r.Service.DeleteScenario(r.Admin.Token, new(r.Generation, other.Id, other.Version)));
        Assert.True(r.Job(job.Id).Active); Assert.Empty(r.Driver.Sent);
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id));
        var manual = r.Service.Submit(r.Admin.Token, r.Manual(delay: 60000));
        Assert.True(r.Service.DeleteScenario(r.Admin.Token, new(r.Generation, definition.Id, definition.Version)));
        Assert.True(r.Job(manual.Id).Active);
    }

    [Fact]
    public async Task Sending_and_stop_requested_execution_block_deletion_until_result_is_recorded()
    {
        using var r = new Rig(); r.Device(); r.Driver.Hold = true;
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1), new("light", DeviceOperation.Brightness, 70));
        var job = r.SubmitScenario(definition);
        var dispatch = r.Service.DispatchNextAsync(); await r.Driver.Started.Task;
        var request = new DeleteScenarioRequest(r.Generation, definition.Id, definition.Version);
        Rig.Reject("scenario_in_use", () => r.Service.DeleteScenario(r.Admin.Token, request));
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id));
        Rig.Reject("scenario_in_use", () => r.Service.DeleteScenario(r.Admin.Token, request));
        r.Driver.Completion.SetResult(new(StepStatus.Simulated, "Test completed")); await dispatch;
        Assert.True(r.Service.DeleteScenario(r.Admin.Token, request));
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Simulated, r.Job(job.Id).Steps[0].Status);
        Assert.Single(r.Driver.Sent); Assert.False(await r.Service.DispatchNextAsync());
    }
}
