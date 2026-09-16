using System.Text.Json;
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
    private sealed class Probe : IHiperwallReader, IHiperwallWriter, IDisposable
    {
        private readonly HiperwallHttpReader _inner = new();
        public bool LoseOpen;
        public Func<Task>? BeforeRead, BeforeWrite;
        public async Task<HiperwallReading> ReadAsync(HiperwallConfiguration c, string? s, CancellationToken ct)
        {
            if (BeforeRead is { } action) await action().WaitAsync(ct);
            return await _inner.ReadAsync(c, s, ct);
        }
        public async Task<HiperwallWriteResult> WriteAsync(HiperwallConfiguration c, string? s, HiperwallWireCommand command, CancellationToken ct)
        {
            if (BeforeWrite is { } action) await action().WaitAsync(ct);
            var result = await _inner.WriteAsync(c, s, command, ct);
            return LoseOpen && command.Action == HiperwallEditAction.Open ? new(HiperwallSendState.Unknown, "lost reply") : result;
        }
        public void Dispose() => _inner.Dispose();
    }
    private static ScenarioStep Wait(int timeout = 5000, FailurePolicy policy = FailurePolicy.Stop) =>
        new("light", DeviceOperation.Power, 1, TimeoutMs: timeout, OnFailure: policy, Kind: ScenarioStepKind.WaitUntil);
    private static ScenarioStep Show(SavedHiperwallLayout layout, int delay = 0, int timeout = 10000, FailurePolicy policy = FailurePolicy.Stop) =>
        new("", default, 0, delay, timeout, policy, Kind: ScenarioStepKind.DisplayLayout, LayoutId: layout.Id);
    private static ScenarioStep Command => new("light", DeviceOperation.Brightness, 60);
    private static HiperwallPlacement Placement => new("uuid", "source-1", "zone-1", new(-25.5, 0, 640, 360));
    private static SavedHiperwallLayout Configure(Rig r, string endpoint, int count = 1)
    {
        r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 0, "Controller", endpoint,
            HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        return r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, Guid.NewGuid(), 0, 1,
            "원본 배치", Enumerable.Range(0, count).Select(i => Placement with { Layout = new(-25.5 + i * 640, 0, 640, 360) }).ToArray(), new()));
    }
    private static HiperwallDisplayJob Display(Rig r) => Assert.Single(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs);

    [Fact]
    public async Task Wait_polls_fresh_state_without_sending_and_allows_unrelated_work()
    {
        using var r = new Rig(); var d = r.Device(); r.Device("other"); r.Driver.ReadPower = 0;
        var j = r.SubmitScenario(r.Scenario(Wait(), Command));
        await r.Service.DispatchNextAsync();
        var waiting = r.Job(j.Id).Steps[0];
        Assert.Equal(StepStatus.Waiting, waiting.Status); Assert.Null(waiting.SentAt);
        Assert.Equal(r.Clock.GetUtcNow().AddSeconds(5), waiting.DeadlineAt);
        Assert.Empty(r.Driver.Sent); Assert.Empty(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id].Desired);
        var other = r.Service.Submit(r.Admin.Token, r.Manual("other"));
        await r.Service.DispatchNextAsync(); Assert.Equal(JobStatus.Completed, r.Job(other.Id).Status);
        r.Clock.Advance(1); await r.Service.DispatchNextAsync(); Assert.Equal(StepStatus.Waiting, r.Job(j.Id).Steps[0].Status);
        r.Driver.ReadPower = 1; r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.ConditionMet, r.Job(j.Id).Steps[0].Status);
        await r.Service.DispatchNextAsync(); Assert.Equal(JobStatus.Completed, r.Job(j.Id).Status);
        Assert.Equal(2, r.Driver.Sent.Count); Assert.Equal(DeviceOperation.Brightness, r.Driver.Sent.Last().Operation);
    }
    [Theory]
    [InlineData(FailurePolicy.Stop, JobStatus.Interrupted, 0)]
    [InlineData(FailurePolicy.Continue, JobStatus.Completed, 1)]
    public async Task Wait_timeout_has_no_uncertain_device_and_honors_failure_policy(FailurePolicy policy, JobStatus status, int sends)
    {
        using var r = new Rig(); r.Device(); r.Driver.ReadPower = 0;
        var j = r.SubmitScenario(r.Scenario(Wait(1000, policy), Command));
        await r.Service.DispatchNextAsync(); r.Clock.Advance(1); await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Failed, r.Job(j.Id).Steps[0].Status);
        Assert.Equal(status, r.Job(j.Id).Status); Assert.Equal(sends, r.Driver.Sent.Count);
        Assert.Empty(r.Service.GetState(r.Admin.Token).UncertainDevices);
    }
    [Fact]
    public async Task Cancelling_during_read_cannot_resurrect_wait_or_run_next_step()
    {
        using var r = new Rig(); r.Device(); r.Driver.HoldRead = true;
        var j = r.SubmitScenario(r.Scenario(Wait(), Command));
        var pending = r.Service.DispatchNextAsync(); await r.Driver.ReadStarted.Task;
        r.Service.Cancel(r.Admin.Token, new(r.Generation, j.Id));
        r.Driver.ReadContinue.SetResult(); await pending;
        Assert.Equal(JobStatus.Cancelled, r.Job(j.Id).Status); Assert.Empty(r.Driver.Sent);
        Assert.All(r.Job(j.Id).Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
    }
    [Fact]
    public async Task Wait_authority_change_bypasses_continue_and_restart_never_resumes()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.ReadPower = 0;
        var j = r.SubmitScenario(r.Scenario(Wait(policy: FailurePolicy.Continue), Command));
        await r.Service.DispatchNextAsync();
        r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", d.Id, 1));
        r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Interrupted, r.Job(j.Id).Status); Assert.Empty(r.Driver.Sent);
        var next = r.SubmitScenario(r.Scenario(Wait(), Command)); await r.Service.DispatchNextAsync();
        r.Restart(); r.Driver.ReadPower = 1; r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Interrupted, r.Job(next.Id).Status);
        Assert.Empty(r.Service.GetState(r.Admin.Token).UncertainDevices); Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public void Legacy_steps_stay_commands_and_invalid_extensions_are_rejected_atomically()
    {
        using var r = new Rig(); r.Device();
        var legacy = JsonSerializer.Deserialize<ScenarioStep>("""{"roleId":"light","operation":"Power","value":1,"timeoutMs":3000}""", JsonDefaults.Options)!;
        Assert.Equal(ScenarioStepKind.DeviceCommand, legacy.Kind); r.Scenario(legacy);
        Rig.Reject("invalid_condition", () => r.Scenario(Wait() with { Operation = DeviceOperation.Stop, Value = 0 }));
        Rig.Reject("invalid_timing", () => r.Scenario(Wait(3600001)));
        Rig.Reject("invalid_step", () => r.Scenario(Command with { Kind = (ScenarioStepKind)42 }));
        Rig.Reject("invalid_step", () => r.Scenario(Wait() with { LayoutId = Guid.NewGuid() }));
        Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Layout_snapshot_is_frozen_at_start_and_cleanup_outlives_completed_scenario()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint);
        var scenario = r.Scenario(Show(l, delay: 1000), Wait(), Command);
        var request = new SubmitRequest(Guid.NewGuid(), r.Generation, null, ScenarioId: scenario.Id);
        var j = r.Service.Submit(r.Admin.Token, request);
        Assert.Equal(j.Id, r.Service.Submit(r.Admin.Token, request).Id);
        var updated = r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, l.Id, 1, 1, "수정",
            [Placement with { Layout = new(999, 0, 100, 100) }], new(DisplayDurationMode.Continuous)));
        r.Service.DeleteHiperwallLayout(r.Admin.Token, new(r.Generation, l.Id, updated.Version));
        Assert.Empty(f.Commands); r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal("원본 배치", Display(r).Name); Assert.Equal(j.Id, Display(r).ScenarioJobId);
        Assert.Equal(r.Clock.GetUtcNow().AddSeconds(15), Display(r).CloseAt);
        await r.Service.ReconcileHiperwallDisplaysAsync(default); Assert.Equal("-25.5", f.Commands.Single().Element("x")!.Value);
        r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Acknowledged, r.Job(j.Id).Steps[0].Status);
        await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(j.Id).Status); Assert.True(Display(r).Outstanding);
        Assert.Equal("Mixed", r.Job(j.Id).Snapshot.Mode);
        r.Clock.Advance(16); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Display(r).Outstanding); Assert.Equal(2, f.Commands.Count); Assert.Contains("external-1", f.Server.Instances);
    }
    [Fact]
    public async Task Whole_layout_preflight_blocks_missing_content_before_any_open_even_with_continue()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint);
        l = r.Service.SaveHiperwallLayout(r.Admin.Token, new(r.Generation, l.Id, 1, 1, "누락 포함",
            [Placement, Placement with { ContentValue = "missing" }], new()));
        var j = r.SubmitScenario(r.Scenario(Show(l, policy: FailurePolicy.Continue), Command));
        await r.Service.DispatchNextAsync(); await r.Service.DispatchNextAsync();
        Assert.Empty(f.Commands); Assert.Empty(r.Driver.Sent); Assert.Empty(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs);
        Assert.Equal(JobStatus.Interrupted, r.Job(j.Id).Status);
    }
    [Fact]
    public async Task Manual_display_is_reserved_until_explicit_switch_and_no_pending_open_is_replayed()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        var l = Configure(r, f.Server.Endpoint); var j = r.SubmitScenario(r.Scenario(Show(l)));
        var error = await Assert.ThrowsAsync<DomainException>(() => r.Service.DisplayHiperwallAsync(r.Admin.Token,
            new(Guid.NewGuid(), r.Generation, 1, l.Id, l.Version), default));
        Assert.Equal("hiperwall_reserved", error.Code);
        await r.Service.DispatchNextAsync();
        r.Service.BeginManualSwitch(r.Admin.Token, new(r.Generation, j.Id));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Empty(f.Commands); Assert.Equal(JobStatus.Cancelled, r.Job(j.Id).Status); Assert.False(Display(r).Outstanding);
        await r.Service.DisplayHiperwallAsync(r.Admin.Token, new(Guid.NewGuid(), r.Generation, 1, l.Id, l.Version), default);
        await r.Service.ReconcileHiperwallDisplaysAsync(default); Assert.Single(f.Commands);
    }
    [Fact]
    public async Task Release_logout_and_handover_preserve_original_scenario_display_requester()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        var l = Configure(r, f.Server.Endpoint); var j = r.SubmitScenario(r.Scenario(Show(l)));
        var original = r.Admin.Session; r.Service.Release(r.Admin.Token, r.Generation); r.Service.Logout(r.Admin.Token);
        await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        var next = r.Login(); r.Generation = r.Service.Acquire(next.Token).Generation;
        var display = Assert.Single(r.Service.GetHiperwallDisplays(next.Token).Jobs);
        Assert.Equal(original.Id, display.Requester.Id); Assert.Equal(j.Snapshot.LeaseGeneration, display.Request.Generation);
        r.Service.StopHiperwallDisplay(next.Token, new(r.Generation, display.Request.RequestId));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(r.Service.GetHiperwallDisplays(next.Token).Jobs.Single().Outstanding);
    }
    [Fact]
    public async Task Unknown_open_latches_parent_failure_even_if_inventory_resolves_before_next_poll()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe { LoseOpen = true }; using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint, count: 2);
        var j = r.SubmitScenario(r.Scenario(Show(l, policy: FailurePolicy.Continue), Command));
        await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(JobStatus.NeedsReview, r.Job(j.Id).Status); Assert.Equal(StepStatus.Unknown, r.Job(j.Id).Steps[0].Status);
        Assert.Equal(HiperwallSendState.Rejected, Display(r).Targets[1].OpenState);
        await r.Service.ReconcileHiperwallDisplaysAsync(default); // Source and ID match; resolves child only.
        Assert.Equal(HiperwallSendState.Acknowledged, Display(r).Targets[0].OpenState);
        r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent); Assert.Single(f.Commands); Assert.Equal(JobStatus.NeedsReview, r.Job(j.Id).Status);
    }
    [Fact]
    public async Task Cancel_inflight_open_holds_reservation_until_result_and_preserves_close_schedule()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        var l = Configure(r, f.Server.Endpoint, count: 2); var j = r.SubmitScenario(r.Scenario(Show(l)));
        await r.Service.DispatchNextAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.BeforeWrite = () => { started.TrySetResult(); return release.Task; };
        var send = r.Service.ReconcileHiperwallDisplaysAsync(default); await started.Task;
        r.Service.Cancel(r.Admin.Token, new(r.Generation, j.Id));
        Assert.Equal(JobStatus.StopRequested, r.Job(j.Id).Status);
        await r.Service.DispatchNextAsync(); Assert.True(r.Job(j.Id).Active);
        release.SetResult(); await send; p.BeforeWrite = null;
        r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Cancelled, r.Job(j.Id).Status); Assert.Single(f.Commands); Assert.True(Display(r).Outstanding);
        r.Clock.Advance(15); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.False(Display(r).Outstanding);
    }
    [Fact]
    public async Task Cancel_during_preflight_never_inserts_child()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        var l = Configure(r, f.Server.Endpoint); var j = r.SubmitScenario(r.Scenario(Show(l)));
        p.BeforeRead = () => { r.Service.Cancel(r.Admin.Token, new(r.Generation, j.Id)); return Task.CompletedTask; };
        await r.Service.DispatchNextAsync();
        Assert.Empty(r.Service.GetHiperwallDisplays(r.Admin.Token).Jobs); Assert.Empty(f.Commands);
        Assert.Equal(JobStatus.Cancelled, r.Job(j.Id).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_authority_or_connection_before_open_stops_continue(bool revoke)
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint);
        var j = r.SubmitScenario(r.Scenario(Show(l, policy: FailurePolicy.Continue), Command));
        await r.Service.DispatchNextAsync();
        if (revoke)
            r.Service.UpdateAccount(r.Admin.Token, new(r.Generation, r.Admin.Session.UserId, true, false, []));
        else
            r.Service.SaveHiperwallSettings(r.Admin.Token, new(r.Generation, 1, "변경", f.Server.Endpoint,
                HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
        await r.Service.ReconcileHiperwallDisplaysAsync(default); r.Clock.Advance(1); await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Interrupted, r.Job(j.Id).Status); Assert.Empty(f.Commands); Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Restart_preserves_opened_display_cleanup_but_never_resumes_parent()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint, count: 2);
        var j = r.SubmitScenario(r.Scenario(Show(l), Command));
        await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        r.Restart(); r.Clock.Advance(1); await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(JobStatus.Interrupted, r.Job(j.Id).Status); Assert.Single(f.Commands); Assert.Empty(r.Driver.Sent);
        Assert.Equal(HiperwallSendState.Rejected, Display(r).Targets[1].OpenState);
        r.Clock.Advance(15); await r.Service.ReconcileHiperwallDisplaysAsync(default); Assert.False(Display(r).Outstanding);
    }
    [Fact]
    public async Task Stopping_linked_display_stops_parent_and_closes_only_owned_instance()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint);
        var j = r.SubmitScenario(r.Scenario(Show(l), Wait()));
        await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        r.Service.StopHiperwallDisplay(r.Admin.Token, new(r.Generation, Display(r).Request.RequestId));
        await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(JobStatus.Cancelled, r.Job(j.Id).Status); Assert.False(Display(r).Outstanding);
        Assert.Contains("external-1", f.Server.Instances); Assert.Empty(r.Driver.Sent);
    }

    [Fact]
    public async Task Display_deadline_blocks_open_even_before_scenario_worker_polls_again()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint);
        var j = r.SubmitScenario(r.Scenario(Show(l, timeout: 1000, policy: FailurePolicy.Continue), Command));
        await r.Service.DispatchNextAsync(); r.Clock.Advance(1);
        await r.Service.ReconcileHiperwallDisplaysAsync(default); await r.Service.DispatchNextAsync();
        Assert.Empty(f.Commands); Assert.Equal(StepStatus.Failed, r.Job(j.Id).Steps[0].Status);
        Assert.Equal(JobStatus.Completed, r.Job(j.Id).Status); Assert.Single(r.Driver.Sent);
    }
    [Fact]
    public async Task Known_controller_rejection_honors_continue_without_retrying_open()
    {
        await using var f = new HiperwallEditorFixture(); using var p = new Probe(); using var r = new Rig(p, new Secrets());
        r.Device(); var l = Configure(r, f.Server.Endpoint);
        f.CommandResponse = _ => new("<Error><code>403</code></Error>");
        var j = r.SubmitScenario(r.Scenario(Show(l, policy: FailurePolicy.Continue), Command));
        await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        await r.Service.DispatchNextAsync(); await r.Service.ReconcileHiperwallDisplaysAsync(default);
        Assert.Equal(StepStatus.Failed, r.Job(j.Id).Steps[0].Status); Assert.Equal(JobStatus.Completed, r.Job(j.Id).Status);
        Assert.Single(f.Commands); Assert.Single(r.Driver.Sent); Assert.False(Display(r).Outstanding);
    }
    [Fact]
    public async Task Wait_revalidates_after_read_before_accepting_condition()
    {
        using var r = new Rig(); var d = r.Device(); r.Driver.HoldRead = true;
        var j = r.SubmitScenario(r.Scenario(Wait(policy: FailurePolicy.Continue), Command));
        var pending = r.Service.DispatchNextAsync(); await r.Driver.ReadStarted.Task;
        r.Service.SaveRole(r.Admin.Token, new(r.Generation, "light", d.Id, 1));
        r.Driver.ReadContinue.SetResult(); await pending; await r.Service.DispatchNextAsync();
        Assert.Equal(StepStatus.Skipped, r.Job(j.Id).Steps[0].Status);
        Assert.Equal(JobStatus.Interrupted, r.Job(j.Id).Status); Assert.Empty(r.Driver.Sent);
    }
}
