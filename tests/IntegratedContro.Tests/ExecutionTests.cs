using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public void All_future_targets_and_alias_roles_are_reserved_but_unrelated_manual_is_allowed()
    {
        using var r = new Rig(); r.Device(); var other = r.Device("other"); r.Device("free");
        r.Service.SaveRole(r.Admin.Token, new(r.Generation, "alias", other.Id));
        var job = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 60000), new("other", DeviceOperation.Power, 1)));
        Rig.Reject("device_reserved", () => r.Service.Submit(r.Admin.Token, r.Manual("alias")));
        Assert.Equal(JobStatus.Queued, r.Service.Submit(r.Admin.Token, r.Manual("free")).Status);
        Assert.Equal(JobStatus.Queued, r.Job(job.Id).Status); // A rejected/conflicting click did not stop the scenario.
    }
    [Fact]
    public async Task Explicit_manual_switch_requires_reconciliation_and_a_new_click()
    {
        using var r = new Rig(); var d = r.Device();
        var job = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, 10000)));
        r.Service.BeginManualSwitch(r.Admin.Token, new(r.Generation, job.Id));
        Assert.Empty(r.Driver.Sent);
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status);
        Rig.Reject("device_uncertain", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        Assert.Empty(r.Driver.Sent);
        Assert.False(await r.Service.DispatchNextAsync());
        r.Service.Submit(r.Admin.Token, r.Manual());
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent);
    }
    [Fact]
    public async Task Cancellation_during_dispatch_keeps_sent_result_and_blocks_late_response_continuation()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.Hold = true;
        var job = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue),
            new("light", DeviceOperation.Brightness, 20)));
        var dispatch = r.Service.DispatchNextAsync();
        await r.Driver.Started.Task;
        r.Service.BeginManualSwitch(r.Admin.Token, new(r.Generation, job.Id));
        Assert.Equal(JobStatus.StopRequested, r.Job(job.Id).Status);
        Rig.Reject("device_reserved", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        r.Driver.Completion.SetResult(new(DriverStatus.Simulated, "late simulated result"));
        await dispatch;
        Assert.Equal(StepStatus.Simulated, r.Job(job.Id).Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[1].Status);
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status);
        Assert.False(await r.Service.DispatchNextAsync());
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        Assert.Single(r.Driver.Sent);
    }
    [Fact]
    public async Task Cancellation_before_dispatch_sends_nothing()
    {
        using var r = new Rig(); r.Device();
        var job = r.Service.Submit(r.Admin.Token, r.Manual());
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id));
        Assert.False(await r.Service.DispatchNextAsync());
        Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Concurrent_worker_calls_do_not_double_send()
    {
        using var r = new Rig(); r.Device(); r.Driver.Hold = true;
        r.Service.Submit(r.Admin.Token, r.Manual());
        var first = r.Service.DispatchNextAsync();
        await r.Driver.Started.Task;
        Assert.False(await r.Service.DispatchNextAsync());
        r.Driver.Completion.SetResult(new(DriverStatus.Simulated, "done"));
        await first; Assert.Single(r.Driver.Sent);
    }
    [Theory]
    [InlineData("device")]
    [InlineData("role")]
    [InlineData("scenario")]
    public async Task Target_or_definition_change_never_mutates_snapshot_or_bypasses_validation(string change)
    {
        using var r = new Rig(); var d = r.Device(); var replacement = r.Device("other");
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue),
            new("light", DeviceOperation.Brightness, 40));
        var job = r.SubmitScenario(definition);
        var original = JsonDefaults.Copy(job.Snapshot);
        if (change == "device")
            r.Service.SaveDevice(r.Admin.Token, new(r.Generation, d.Id, d.PcId, d.PcName, d.Name, "new-address", "test", ExpectedVersion: d.Version));
        else if (change == "role") r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", replacement.Id, 1));
        else r.Service.SaveScenario(r.Admin.Token, new(r.Generation, definition.Id, "Changed", [new("light", DeviceOperation.Power, 0)], definition.Version));
        await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent);
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
        Assert.Equal(original.Steps[0], r.Job(job.Id).Snapshot.Steps[0]);
        Assert.Equal(original.ScenarioVersion, r.Job(job.Id).Snapshot.ScenarioVersion);
    }
    [Fact]
    public async Task Revocation_while_reading_condition_cannot_be_bypassed_by_continue()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.HoldRead = true;
        var definition = r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue,
            ConditionOperation: DeviceOperation.Power, ConditionValue: 1), new("light", DeviceOperation.Brightness, 40));
        var job = r.SubmitScenario(definition);
        var dispatch = r.Service.DispatchNextAsync();
        await r.Driver.ReadStarted.Task;
        r.Service.SaveDevice(r.Admin.Token, new(r.Generation, d.Id, d.PcId, d.PcName, d.Name, "changed", "test", ExpectedVersion: d.Version));
        r.Driver.ReadContinue.SetResult();
        await dispatch;
        Assert.Empty(r.Driver.Sent);
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[1].Status);
    }
    [Fact]
    public async Task Unknown_result_stops_continue_scenario_and_requires_target_reconciliation()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.NextStatus = DriverStatus.Unknown;
        var job = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue),
            new("light", DeviceOperation.Brightness, 80)));
        await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.NeedsReview, r.Job(job.Id).Status);
        Rig.Reject("device_uncertain", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        Assert.Equal(JobStatus.NeedsReview, r.Job(job.Id).Status); // Query never rewrites historical uncertainty as success.
        Assert.False(await r.Service.DispatchNextAsync());
    }
    [Fact]
    public async Task Stop_has_priority_and_blocks_reserved_scenario_followup()
    {
        using var r = new Rig(); r.Device(); r.Device("other");
        var scenario = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Lift, 1, 1000), new("light", DeviceOperation.Lift, -1)));
        r.Service.Submit(r.Admin.Token, r.Manual("other"));
        r.Service.Submit(r.Admin.Token, r.Manual() with { Operation = DeviceOperation.Stop, Value = 0 });
        await r.Service.DispatchNextAsync();
        Assert.Equal(DeviceOperation.Stop, Assert.Single(r.Driver.Sent).Operation);
        Assert.Equal(JobStatus.Cancelled, r.Job(scenario.Id).Status);
    }
    [Fact]
    public async Task Expired_job_is_not_sent()
    {
        using var r = new Rig(); r.Device();
        var job = r.Service.Submit(r.Admin.Token, r.Manual() with { ExpiresAfterSeconds = 1 });
        r.Clock.Advance(2); await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent); Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
    }
    [Fact]
    public void Returned_snapshots_cannot_mutate_service_state()
    {
        using var r = new Rig(); r.Device();
        var job = r.Service.Submit(r.Admin.Token, r.Manual());
        job.Snapshot.Steps[0] = job.Snapshot.Steps[0] with { Value = 0 };
        job.Status = JobStatus.Cancelled;
        Assert.Equal(1, r.Job(job.Id).Snapshot.Steps[0].Value);
        Assert.Equal(JobStatus.Queued, r.Job(job.Id).Status);
    }
}
