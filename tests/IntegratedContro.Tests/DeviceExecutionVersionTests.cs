using System.Text.Json;
using System.Text.Json.Nodes;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class DeviceExecutionVersionTests
{
    private static DeviceConfig Save(Rig r, DeviceConfig d) => r.Service.SaveDevice(r.Admin.Token,
        new(r.Generation, d.Id, d.PcId, d.PcName, d.Name, d.ConnectionId, d.ModelId,
            d.Enabled, d.Fault, d.LatencyMs, d.Version));

    [Theory]
    [InlineData("pc", StepStatus.Simulated)]
    [InlineData("connection", StepStatus.Simulated)]
    [InlineData("fault", StepStatus.Failed)]
    [InlineData("pc", StepStatus.Unknown)]
    [InlineData("roundtrip", StepStatus.Simulated)]
    public async Task Late_result_stays_with_original_job_and_never_updates_reconfigured_target(string change, StepStatus status)
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.Hold = true;
        var job = r.Service.Submit(r.Admin.Token, r.Manual() with { TimeoutMs = 30000 });
        var dispatch = r.Service.DispatchNextAsync();
        await r.Driver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var changed = Save(r, change switch
            {
                "connection" => d with { ConnectionId = "other-connection" },
                "fault" => d with { Fault = VirtualFault.Failure },
                _ => d with { PcId = Guid.NewGuid(), PcName = "PC B" }
            });
            if (change == "roundtrip") changed = Save(r, d with { Version = changed.Version });
            Assert.True(changed.ExecutionVersion > d.ExecutionVersion);
            var before = JsonSerializer.Serialize(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id], JsonDefaults.Options);
            r.Driver.Completion.SetResult(new(status, "original PC response",
                new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 1 }));
            await dispatch;
            var state = r.Service.GetState(r.Admin.Token);
            Assert.Equal(before, JsonSerializer.Serialize(state.DeviceStates[d.Id], JsonDefaults.Options));
            Assert.DoesNotContain(d.Id, state.UncertainDevices);
            Assert.Equal(d, r.Job(job.Id).Snapshot.Steps[0].Target);
            Assert.Equal(status, r.Job(job.Id).Steps[0].Status);
            Assert.Equal("original PC response", r.Job(job.Id).Steps[0].Result);
        }
        finally { r.Driver.Completion.TrySetResult(new(StepStatus.Simulated, "cleanup")); await dispatch; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Display_rename_preserves_state_and_runs_accepted_manual_or_scenario(bool scenario)
    {
        using var r = new Rig(); var d = r.Device();
        await r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        var job = scenario ? r.SubmitScenario(r.Scenario(new("light", DeviceOperation.Power, 1),
            new("light", DeviceOperation.Brightness, 25))) : r.Service.Submit(r.Admin.Token, r.Manual());
        var before = JsonSerializer.Serialize(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id], JsonDefaults.Options);
        var renamed = Save(r, d with { Name = "renamed light", PcName = "renamed PC" });
        Assert.Equal(d.Version + 1, renamed.Version);
        Assert.Equal(d.ExecutionVersion, renamed.ExecutionVersion);
        Assert.Equal(before, JsonSerializer.Serialize(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id], JsonDefaults.Options));
        Rig.Reject("version_conflict", () => Save(r, d));
        while (await r.Service.DispatchNextAsync()) { }
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status);
        Assert.Equal(scenario ? 2 : 1, r.Driver.Sent.Count);
        Assert.All(r.Driver.Sent, s => Assert.Equal(d, s.Target));
    }

    [Fact]
    public async Task Rename_during_dispatch_allows_the_matching_result_to_update_current_state()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.Hold = true;
        r.Service.Submit(r.Admin.Token, r.Manual() with { TimeoutMs = 30000 });
        var dispatch = r.Service.DispatchNextAsync(); await r.Driver.Started.Task;
        Save(r, d with { Name = "new display name" });
        r.Driver.Completion.SetResult(new(StepStatus.Simulated, "matching response",
            new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 1 }));
        await dispatch;
        var state = r.Service.GetState(r.Admin.Token).DeviceStates[d.Id];
        Assert.Equal(1, state.Simulated[DeviceOperation.Power].Value);
        Assert.Equal("matching response", state.LastResult);
    }

    [Theory]
    [InlineData("pc")]
    [InlineData("connection")]
    [InlineData("enabled")]
    [InlineData("fault")]
    [InlineData("latency")]
    [InlineData("roundtrip")]
    public async Task Execution_setting_changes_still_block_accepted_work_even_after_rename(string field)
    {
        using var r = new Rig(); var d = r.Device();
        var job = r.SubmitScenario(r.Scenario(new("light", DeviceOperation.Power, 1, OnFailure: FailurePolicy.Continue),
            new("light", DeviceOperation.Brightness, 30)));
        var renamed = Save(r, d with { Name = "renamed" });
        var changed = Save(r, field switch
        {
            "pc" or "roundtrip" => renamed with { PcId = Guid.NewGuid() },
            "connection" => renamed with { ConnectionId = "changed" },
            "enabled" => renamed with { Enabled = false },
            "fault" => renamed with { Fault = VirtualFault.Failure },
            _ => renamed with { LatencyMs = 100 }
        });
        if (field == "roundtrip") Save(r, renamed with { Version = changed.Version });
        await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
        Assert.Empty(r.Driver.Sent);
        Assert.All(r.Job(job.Id).Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_accepts_display_rename_but_rejects_target_change(bool changeTarget)
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.HoldRead = true;
        var reading = r.Service.ReconcileAsync(r.Admin.Token, new(r.Generation, d.Id));
        await r.Driver.ReadStarted.Task;
        Save(r, d with { Name = "renamed", PcId = changeTarget ? Guid.NewGuid() : d.PcId });
        r.Driver.ReadContinue.SetResult();
        if (changeTarget)
        {
            Assert.Equal("reconcile_changed", (await Assert.ThrowsAsync<DomainException>(() => reading)).Code);
            Assert.Empty(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id].Simulated);
        }
        else Assert.Equal(1, (await reading).Simulated[DeviceOperation.Power].Value);
    }

    [Fact]
    public async Task Legacy_json_defaults_execution_version_and_rename_survives_restart()
    {
        using var r = new Rig(); var d = r.Device();
        r.Service.Submit(r.Admin.Token, r.Manual());
        var node = JsonNode.Parse(JsonSerializer.Serialize(r.Store.Load(), JsonDefaults.Options))!;
        node["devices"]![0]!.AsObject().Remove("executionVersion");
        node["jobs"]![0]!["snapshot"]!["steps"]![0]!["target"]!.AsObject().Remove("executionVersion");
        r.Store.Save(node.Deserialize<HostState>(JsonDefaults.Options)!);
        r.Restart();
        var review = r.Service.ReviewRecovery(r.Admin.Token);
        r.Service.ApproveRecovery(r.Admin.Token, review.ReviewId);
        r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        var loaded = r.Service.GetState(r.Admin.Token).Devices.Single();
        Assert.Equal(loaded.Version, loaded.ExecutionVersion);
        Save(r, loaded with { Name = "legacy rename" });
        r.Service.Release(r.Admin.Token, r.Generation);
        r.Restart();
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent);
        Assert.Equal(JobStatus.Completed, r.Service.GetState(r.Admin.Token).Jobs.Single().Status);
    }

    [Fact]
    public void Restart_marks_old_dispatch_unknown_without_attributing_it_to_replacement()
    {
        using var r = new Rig(); var d = r.Device();
        var job = r.Service.Submit(r.Admin.Token, r.Manual());
        Save(r, d with { PcId = Guid.NewGuid(), PcName = "replacement" });
        var persisted = r.Store.Load();
        persisted.Jobs.Single().Steps[0].Status = StepStatus.Dispatching;
        persisted.Jobs.Single().Steps[0].SentAt = r.Clock.GetUtcNow();
        persisted.Jobs.Single().Status = JobStatus.Running;
        r.Store.Save(persisted); r.Restart();
        var state = r.Service.GetState(r.Admin.Token);
        Assert.Empty(state.DeviceStates[d.Id].Simulated);
        Assert.Contains("새 상태 조회 필요", state.DeviceStates[d.Id].Connection);
        Assert.DoesNotContain(d.Id, state.UncertainDevices);
        Assert.Equal(JobStatus.NeedsReview, r.Job(job.Id).Status);
        Assert.Equal(d.PcId, r.Job(job.Id).Snapshot.Steps[0].Target.PcId);
    }
}
