using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class ScenarioExtensionTests
{
    private sealed class Secrets : ICredentialStore
    {
        public Guid Save(string secret) => Guid.NewGuid();
        public string Read(Guid reference) => FakeHiperwallServer.FixtureSecret;
    }
    private static ScenarioStep Wait(FailurePolicy policy = FailurePolicy.Stop, int timeout = 3000) =>
        new("light", DeviceOperation.Power, 1, TimeoutMs: timeout, OnFailure: policy)
        { Kind = ScenarioStepKind.WaitUntil, PollIntervalMs = 1000 };
    private static ScenarioStep Display(SavedHiperwallLayout layout, FailurePolicy policy = FailurePolicy.Stop) =>
        new("", DeviceOperation.Power, 0, TimeoutMs: 60000, OnFailure: policy)
        { Kind = ScenarioStepKind.ShowLayout, SavedLayoutId = layout.Id };
    private static void Configure(Rig r, HiperwallEditorFixture fixture) =>
        r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 0, "scenario fixture", fixture.Server.Endpoint,
            HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
    private static async Task<SavedHiperwallLayout> Capture(Rig r, Guid? id = null, int version = 0)
    {
        var view = await r.Service.RefreshHiperwallAsync(r.Admin.Token, false, default);
        return await r.Service.CaptureHiperwallLayoutAsync(r.Admin.Token,
            new(r.Generation, id ?? Guid.NewGuid(), "운영 배치", version, 1, HiperwallEditing.Revision(view.Instances.Items)), default);
    }
    private static async Task Drain(Rig r, Guid id)
    {
        for (var i = 0; i < 15 && r.Job(id).Active; i++)
        {
            await r.Service.DispatchNextAsync();
            await r.Service.DispatchHiperwallNextAsync(default);
        }
        Assert.False(r.Job(id).Active);
    }
    [Fact]
    public async Task Wait_polls_without_writes_keeps_reservation_and_allows_other_devices_then_advances()
    {
        using var r = new Rig(); r.Device(); r.Device("other"); r.Driver.ReadPower = 0;
        var job = r.SubmitScenario(r.Scenario(Wait(), new("light", DeviceOperation.Power, 0)));
        await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Waiting, r.Job(job.Id).Steps[0].Status);
        Assert.Null(r.Job(job.Id).Steps[0].SentAt); Assert.Empty(r.Driver.Sent);
        Rig.Reject("device_reserved", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        r.Service.Submit(r.Admin.Token, r.Manual("other")); await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent); Assert.Equal("other", r.Driver.Sent[0].Role!.Id);
        r.Clock.Advance(1); r.Driver.ReadPower = 1; await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Simulated, r.Job(job.Id).Steps[0].Status);
        Assert.Equal(2, r.Job(job.Id).Steps[0].ObservationsChecked); Assert.Null(r.Job(job.Id).Steps[0].SentAt);
        await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status); Assert.Equal(2, r.Driver.Sent.Count);
    }
    [Theory]
    [InlineData(FailurePolicy.Stop, 0, JobStatus.Interrupted)]
    [InlineData(FailurePolicy.Continue, 1, JobStatus.Completed)]
    public async Task Condition_timeout_has_no_uncertain_write_and_obeys_failure_policy(FailurePolicy policy, int sent, JobStatus status)
    {
        using var r = new Rig(); r.Device(); r.Driver.ReadPower = 0;
        var job = r.SubmitScenario(r.Scenario(Wait(policy), new("light", DeviceOperation.Power, 0)));
        await r.Service.DispatchNextAsync(); r.Clock.Advance(3); await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Equal(status, r.Job(job.Id).Status);
        Assert.Equal(CommandOutcome.TimedOut, r.Job(job.Id).Steps[0].Evidence!.Outcome);
        Assert.Empty(r.Store.Load().UncertainDevices); Assert.Equal(sent, r.Driver.Sent.Count);
        Assert.Null(r.Job(job.Id).Steps[0].SentAt);
    }
    [Fact]
    public async Task Cancellation_during_condition_read_wins_over_late_success()
    {
        using var r = new Rig(); r.Device(); r.Driver.HoldRead = true;
        var job = r.SubmitScenario(r.Scenario(Wait(), new("light", DeviceOperation.Power, 0)));
        var poll = r.Service.DispatchNextAsync(); await r.Driver.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id)); r.Driver.ReadContinue.SetResult(); await poll;
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status);
        Assert.All(r.Job(job.Id).Steps, s => Assert.Equal(StepStatus.Skipped, s.Status)); Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Wait_survives_release_but_is_not_resumed_after_host_restart()
    {
        using var r = new Rig(); r.Device(); r.Driver.ReadPower = 0;
        var job = r.SubmitScenario(r.Scenario(Wait()));
        await r.Service.DispatchNextAsync(); var deadline = r.Job(job.Id).Steps[0].WaitDeadline;
        r.Service.Release(r.Admin.Token, r.Generation); r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Waiting, r.Job(job.Id).Steps[0].Status); Assert.Equal(deadline, r.Job(job.Id).Steps[0].WaitDeadline);
        r.Restart(); Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[0].Status); Assert.False(await r.Service.DispatchNextAsync());
    }
    [Fact]
    public async Task Saved_layout_capture_is_read_only_and_display_uses_frozen_version_after_edit_and_release()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture);
        var layout = await Capture(r); Assert.Empty(fixture.Commands);
        var job = r.SubmitScenario(r.Scenario(Display(layout)));
        fixture.Server.Instances = fixture.Server.Instances.Replace("-960,0", "200,-300");
        var updated = await Capture(r, layout.Id, 1); Assert.Equal(2, updated.Version);
        r.Service.Release(r.Admin.Token, r.Generation); await Drain(r, job.Id);
        var command = fixture.Commands.Single();
        Assert.Equal("-960", command.Element("x")!.Value); Assert.Equal("0", command.Element("y")!.Value);
        Assert.Contains("external-1", fixture.Server.Instances);
        Assert.Equal(1, r.Job(job.Id).Snapshot.Steps[0].SavedLayout!.Version);
        Assert.Equal(ConfirmationLevel.ProtocolAcknowledged, r.Job(job.Id).Steps[0].Evidence!.Confirmation);
        Assert.Equal(1, r.Service.GetJobHistory(r.Admin.Token, new()).Items.Single().Value.Snapshot.Steps[0].SavedLayout!.Version);
    }
    [Fact]
    public async Task Mixed_wait_display_and_device_sequence_preserves_order_and_never_calls_device_driver_for_layout()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture); r.Device();
        var layout = await Capture(r);
        var job = r.SubmitScenario(r.Scenario(Wait(), Display(layout), new("light", DeviceOperation.Power, 0)));
        await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent); Assert.Empty(fixture.Commands);
        Assert.Equal(StepStatus.Dispatching, r.Job(job.Id).Steps[1].Status);
        Assert.False(await r.Service.DispatchNextAsync()); // Cannot overtake outstanding Hiperwall receipt.
        await r.Service.DispatchHiperwallNextAsync(default); await Drain(r, job.Id);
        Assert.Single(fixture.Commands); Assert.Single(r.Driver.Sent); Assert.Equal(0, r.Driver.Sent[0].Value);
        Assert.Equal("Mixed", r.Job(job.Id).Snapshot.Mode);
    }
    [Fact]
    public async Task Layout_partial_unknown_stops_remaining_items_and_ignores_continue_policy()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        fixture.Server.Instances = fixture.Server.Instances.Replace("</Objects>", fixture.Server.Instances.Replace("external-1", "external-2").Replace("<Objects>", ""));
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture); r.Device();
        var layout = await Capture(r); Assert.Equal(2, layout.Entries.Length);
        var job = r.SubmitScenario(r.Scenario(Display(layout, FailurePolicy.Continue), new("light", DeviceOperation.Power, 0)));
        fixture.CommandResponse = _ => new("<unexpected/>");
        await Drain(r, job.Id);
        Assert.Equal(JobStatus.NeedsReview, r.Job(job.Id).Status); Assert.Single(fixture.Commands); Assert.Empty(r.Driver.Sent);
        Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[1].Status);
        Assert.False(await r.Service.DispatchNextAsync()); await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Single(fixture.Commands);
    }
    [Fact]
    public async Task Layout_reservation_blocks_manual_edit_and_cancel_stops_pending_child_receipt()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture);
        var layout = await Capture(r); var job = r.SubmitScenario(r.Scenario(Display(layout)));
        Assert.True(r.Service.GetState(r.Admin.Token).HiperwallReserved);
        var edit = new HiperwallEditRequest(Guid.NewGuid(), r.Generation, 1, HiperwallEditAction.Open,
            Selector: "uuid", ContentValue: "source-1", ZoneId: "zone-1", Layout: new(0,0,640,360));
        Assert.Equal("hiperwall_reserved", (await Assert.ThrowsAsync<DomainException>(() => r.Service.EditHiperwallAsync(r.Admin.Token, edit, default))).Code);
        await r.Service.DispatchNextAsync();
        var child = r.Service.GetHiperwallEdits(r.Admin.Token).Single();
        r.Service.CancelHiperwallEdit(r.Admin.Token, new(r.Generation, child.Request.RequestId));
        await Drain(r, job.Id);
        Assert.Equal(JobStatus.Cancelled, r.Job(job.Id).Status); Assert.Empty(fixture.Commands);
        Assert.Empty(r.Service.GetState(r.Admin.Token).OutstandingHiperwallEdits);
    }
    [Fact]
    public async Task Controller_change_before_display_stops_scenario_despite_continue_policy()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture); r.Device();
        var layout = await Capture(r); var job = r.SubmitScenario(r.Scenario(Display(layout, FailurePolicy.Continue), new("light", DeviceOperation.Power, 0)));
        r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 1, "changed", fixture.Server.Endpoint,
            HiperwallAuthentication.Token, "3", 3000));
        await Drain(r, job.Id);
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status); Assert.Empty(fixture.Commands); Assert.Empty(r.Driver.Sent);
    }
    [Theory]
    [InlineData("content")]
    [InlineData("zone")]
    [InlineData("shadow")]
    public async Task Missing_display_target_or_unwritable_controller_stops_even_with_continue(string missing)
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture); r.Device();
        var layout = await Capture(r);
        var job = r.SubmitScenario(r.Scenario(Display(layout, FailurePolicy.Continue), new("light", DeviceOperation.Power, 0)));
        if (missing == "content") fixture.Server.Contents = "<Objects />";
        else if (missing == "zone") fixture.Server.Walls = "<Zones />";
        else fixture.Server.Hello = "Hiperwall,2026 R2,Token,Shadow";
        await Drain(r, job.Id);
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status); Assert.Empty(fixture.Commands); Assert.Empty(r.Driver.Sent);
        Assert.Contains("후속 단계 차단", r.Job(job.Id).Result);
        Assert.True(r.Job(job.Id).Steps[0].HiperwallResults.Single().BlocksFollowingSteps);
    }
    [Fact]
    public async Task Restart_keeps_inflight_display_unknown_and_does_not_replay_it()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture);
        var layout = await Capture(r);
        var job = r.Service.Submit(r.Admin.Token, new(Guid.NewGuid(), r.Generation, null,
            TimeoutMs:60000, SavedLayoutId:layout.Id, SavedLayoutVersion:layout.Version));
        await r.Service.DispatchNextAsync();
        var interrupted = r.Store.Load();
        interrupted.HiperwallEdits.Single().Steps.Single().State = HiperwallSendState.Sending;
        interrupted.Jobs.Single().Steps[0].SentAt = r.Clock.GetUtcNow(); r.Store.Save(interrupted);
        r.Restart();
        Assert.Equal(JobStatus.NeedsReview, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Unknown, r.Job(job.Id).Steps[0].Status);
        await r.Service.DispatchHiperwallNextAsync(default); Assert.Empty(fixture.Commands);
    }
    [Fact]
    public async Task Restart_does_not_report_partially_acknowledged_layout_as_success()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        fixture.Server.Instances = fixture.Server.Instances.Replace("</Objects>", fixture.Server.Instances.Replace("external-1", "external-2").Replace("<Objects>", ""));
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture);
        var layout = await Capture(r); var job = r.SubmitScenario(r.Scenario(Display(layout)));
        await r.Service.DispatchNextAsync(); await r.Service.DispatchHiperwallNextAsync(default);
        Assert.Single(fixture.Commands); r.Restart();
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
        Assert.Equal(StepStatus.Failed, r.Job(job.Id).Steps[0].Status);
        Assert.Equal(CommandOutcome.Failed, r.Job(job.Id).Steps[0].Evidence!.Outcome);
        Assert.Equal(2, r.Job(job.Id).Steps[0].HiperwallResults.Count);
        await r.Service.DispatchHiperwallNextAsync(default); Assert.Single(fixture.Commands);
    }
    private sealed class ReadingDriver : IDeviceDriver
    {
        public DeviceModel[] Models => [new("test", "관측 검증", [new(DeviceOperation.Power, 0, 1, "on/off")])];
        public required DriverReading Reading { get; set; }
        public int Writes { get; private set; }
        public Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken ct) => Task.FromResult(Reading);
        public Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken ct)
        { Writes++; throw new InvalidOperationException("Wait steps must never write"); }
    }
    [Theory]
    [InlineData("fresh")]
    [InlineData("stale")]
    [InlineData("ack")]
    public async Task Wait_until_requires_fresh_observation_and_preserves_its_evidence(string kind)
    {
        using var r = new Rig(); r.Device();
        var now = r.Clock.GetUtcNow();
        var reading = new DriverReading(true, new Dictionary<DeviceOperation,int> { [DeviceOperation.Power] = 1 }, "실제 조회")
        {
            Confirmation = kind == "ack" ? ConfirmationLevel.ProtocolAcknowledged : ConfirmationLevel.Observed,
            Observations = new Dictionary<DeviceOperation,DeviceObservation>
                { [DeviceOperation.Power] = new(1, "on/off", now.AddSeconds(kind == "stale" ? -10 : 0), now.AddSeconds(kind == "stale" ? -1 : 5)) }
        };
        var driver = new ReadingDriver { Reading = reading };
        var service = new ControlService(r.Store, r.Hasher, driver, r.Clock);
        var login = service.Login(new("admin", Rig.Password, Guid.NewGuid(), "test PC"));
        service.ApproveRecovery(login.Token, service.ReviewRecovery(login.Token).ReviewId);
        var generation = service.Acquire(login.Token).Generation;
        var definition = service.SaveScenario(login.Token, new(generation, Guid.NewGuid(), "관측까지 대기", [Wait()]));
        var job = service.Submit(login.Token, new(Guid.NewGuid(), generation, null, ScenarioId:definition.Id));
        await service.DispatchNextAsync();
        var result = service.GetState(login.Token).Jobs.Single(j => j.Id == job.Id);
        Assert.Equal(kind == "fresh" ? StepStatus.Succeeded : StepStatus.Waiting, result.Steps[0].Status);
        if (kind == "fresh")
            Assert.Equal(reading.Observations[DeviceOperation.Power], result.Steps[0].Evidence!.Observations[DeviceOperation.Power]);
        Assert.Equal(0, driver.Writes); Assert.Null(result.Steps[0].SentAt);
    }
    [Fact]
    public async Task Saved_layout_permissions_versions_and_unsent_restart_fail_closed()
    {
        await using var fixture = new HiperwallEditorFixture(); using var adapter = new HiperwallHttpReader();
        using var r = new Rig(adapter, new Secrets()); Configure(r, fixture);
        var layout = await Capture(r); var viewer = r.Operator("viewer", AccountRole.Viewer);
        var request = new SubmitRequest(Guid.NewGuid(), r.Generation, null, TimeoutMs:60000, SavedLayoutId:layout.Id, SavedLayoutVersion:1);
        Rig.Reject("lease_required", () => r.Service.Submit(viewer.Token, request));
        Rig.Reject("version_conflict", () => r.Service.Submit(r.Admin.Token, request with { SavedLayoutVersion = 0 }));
        var job = r.Service.Submit(r.Admin.Token, request);
        await r.Service.DispatchNextAsync(); r.Restart();
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status); // Pending display is known unsent and never replayed.
        Assert.Equal(StepStatus.Skipped, r.Job(job.Id).Steps[0].Status);
        await r.Service.DispatchHiperwallNextAsync(default); Assert.Empty(fixture.Commands);
        Assert.Equal(layout, r.Store.Load().HiperwallLayouts.Single() with { Entries = layout.Entries });
    }
}
