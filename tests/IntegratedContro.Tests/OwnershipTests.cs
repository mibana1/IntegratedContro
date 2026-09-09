using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class OwnershipTests
{
    [Fact]
    public async Task Concurrent_same_account_sessions_have_exactly_one_owner()
    {
        using var r = new Rig();
        r.Service.Release(r.Admin.Token, r.Generation);
        var clients = Enumerable.Range(0, 16).Select(_ => r.Login()).ToArray();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = clients.Select(c => Task.Run(async () =>
        {
            await go.Task;
            try { r.Service.Acquire(c.Token); return 1; }
            catch (DomainException e) { Assert.Equal("lease_busy", e.Code); return 0; }
        })).ToArray();
        go.SetResult();
        Assert.Equal(1, (await Task.WhenAll(tasks)).Sum());
    }
    [Fact]
    public async Task Different_accounts_race_and_nonowner_cannot_submit()
    {
        using var r = new Rig(); r.Device();
        var b = r.Operator("b"); r.Service.Release(r.Admin.Token, r.Generation);
        var results = await Task.WhenAll(new[] { r.Admin, b }.Select(c => Task.Run(() =>
        {
            try { return (c, Lease: r.Service.Acquire(c.Token)); } catch (DomainException) { return (c, Lease: (Lease?)null); }
        })));
        var winner = Assert.Single(results, x => x.Lease is not null);
        var loser = Assert.Single(results, x => x.Lease is null);
        Rig.Reject("lease_required", () => r.Service.Submit(loser.c.Token, new(Guid.NewGuid(), winner.Lease!.Generation, "light")));
    }
    [Fact]
    public void Release_preserves_work_and_next_operator_can_cancel_with_original_author()
    {
        using var r = new Rig(); r.Device();
        var b = r.Operator("b");
        var job = r.Service.Submit(r.Admin.Token, r.Manual(delay: 60000));
        r.Service.Release(r.Admin.Token, r.Generation);
        var lease = r.Service.Acquire(b.Token);
        var cancelled = r.Service.Cancel(b.Token, new(lease.Generation, job.Id));
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal(r.Admin.Session.UserId, cancelled.Snapshot.RequestedBy);
        Assert.Equal(b.Session.UserId, cancelled.CancelledBy);
        Assert.Equal(r.Admin.Session.Id, cancelled.Snapshot.SessionId);
        Assert.Empty(r.Driver.Sent);
    }
    [Fact]
    public async Task Accepted_manual_and_scenario_continue_after_all_sessions_logout()
    {
        using var r = new Rig(); r.Device(); r.Device("other");
        var manual = r.Service.Submit(r.Admin.Token, r.Manual("other"));
        var scenario = r.SubmitScenario(r.Scenario(new ScenarioStep("light", DeviceOperation.Power, 1), new("light", DeviceOperation.Brightness, 35)));
        r.Service.Logout(r.Admin.Token);
        while (await r.Service.DispatchNextAsync()) { }
        Assert.Equal(3, r.Driver.Sent.Count);
        var observer = r.Login();
        var state = r.Service.GetState(observer.Token);
        Assert.Equal(JobStatus.Completed, state.Jobs.Single(j => j.Id == manual.Id).Status);
        Assert.Equal(JobStatus.Completed, state.Jobs.Single(j => j.Id == scenario.Id).Status);
        Assert.Equal(LeaseMode.Free, state.Lease.Mode);
    }
    [Fact]
    public async Task Duplicate_request_is_one_job_even_with_concurrent_retries_and_release()
    {
        using var r = new Rig(); r.Device();
        var request = r.Manual();
        var jobs = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => r.Service.Submit(r.Admin.Token, request))));
        Assert.Single(jobs.Select(j => j.Id).Distinct());
        r.Service.Release(r.Admin.Token, r.Generation);
        Assert.Equal(jobs[0].Id, r.Service.Submit(r.Admin.Token, request).Id);
        Rig.Reject("request_id_conflict", () => r.Service.Submit(r.Admin.Token, request with { Value = 0 }));
        await r.Service.DispatchNextAsync();
        Assert.Single(r.Driver.Sent);
    }
    [Fact]
    public void Old_generation_request_is_rejected_even_when_same_session_reacquires()
    {
        using var r = new Rig(); r.Device();
        var stale = r.Manual();
        r.Service.Release(r.Admin.Token, r.Generation);
        r.Service.Acquire(r.Admin.Token);
        Rig.Reject("lease_required", () => r.Service.Submit(r.Admin.Token, stale));
        Assert.Empty(r.Service.GetState(r.Admin.Token).Jobs);
    }
    [Fact]
    public void Viewer_cannot_acquire_or_cancel_and_new_owner_needs_target_scope()
    {
        using var r = new Rig(); var light = r.Device(); r.Device("other");
        var viewer = r.Operator("viewer", AccountRole.Viewer);
        var restricted = r.Operator("limited", all: false, ids: [light.Id]);
        var lightJob = r.Service.Submit(r.Admin.Token, r.Manual());
        var otherJob = r.Service.Submit(r.Admin.Token, r.Manual("other"));
        Rig.Reject("lease_required", () => r.Service.Cancel(viewer.Token, new(r.Generation, lightJob.Id)));
        r.Service.Release(r.Admin.Token, r.Generation);
        Rig.Reject("forbidden", () => r.Service.Acquire(viewer.Token));
        var lease = r.Service.Acquire(restricted.Token);
        Rig.Reject("target_forbidden", () => r.Service.Cancel(restricted.Token, new(lease.Generation, otherJob.Id)));
        Assert.Equal(JobStatus.Cancelled, r.Service.Cancel(restricted.Token, new(lease.Generation, lightJob.Id)).Status);
    }
    [Fact]
    public async Task Revoking_original_requester_permission_blocks_dispatch_after_handoff()
    {
        using var r = new Rig(); var d = r.Device();
        var b = r.Operator("b"); r.Service.Release(r.Admin.Token, r.Generation);
        var bLease = r.Service.Acquire(b.Token);
        var job = r.Service.Submit(b.Token, new(Guid.NewGuid(), bLease.Generation, "light"));
        r.Service.Release(b.Token, bLease.Generation);
        r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        r.Service.UpdateAccount(r.Admin.Token, new(r.Generation, b.Session.UserId, true, false, []));
        await r.Service.DispatchNextAsync();
        Assert.Empty(r.Driver.Sent);
        Assert.Equal(JobStatus.Interrupted, r.Job(job.Id).Status);
        Assert.Contains("권한 회수", r.Job(job.Id).Result);
    }
    [Fact]
    public void Late_heartbeat_cannot_revive_session_and_recovery_requires_review()
    {
        using var r = new Rig(); r.Device();
        var acceptedRequest = r.Manual();
        var job = r.Service.Submit(r.Admin.Token, acceptedRequest);
        var stale = r.Manual();
        r.Clock.Advance(16);
        Rig.Reject("session_fenced", () => r.Service.Heartbeat(r.Admin.Token, r.Generation));
        Assert.Equal(LeaseMode.RecoveryRequired, r.Service.GetState(r.Admin.Token).Lease.Mode);
        Rig.Reject("session_fenced", () => r.Service.Submit(r.Admin.Token, stale));
        Assert.Equal(job.Id, r.Service.Submit(r.Admin.Token, acceptedRequest).Id);
        var admin2 = r.Login();
        Rig.Reject("lease_busy", () => r.Service.Acquire(admin2.Token));
        Rig.Reject("review_required", () => r.Service.ApproveRecovery(admin2.Token, Guid.NewGuid()));
        var review = r.Service.ReviewRecovery(admin2.Token);
        r.Service.ApproveRecovery(admin2.Token, review.ReviewId);
        Rig.Reject("session_fenced", () => r.Service.Acquire(r.Admin.Token));
        Assert.Equal(LeaseMode.Held, r.Service.Acquire(admin2.Token).Mode);
        Rig.Reject("session_fenced", () => r.Service.Submit(r.Admin.Token, stale));
    }
    [Fact]
    public async Task Recovery_review_must_be_repeated_if_running_work_changes()
    {
        using var r = new Rig(); r.Device();
        r.Service.Submit(r.Admin.Token, r.Manual());
        r.Clock.Advance(16); r.Service.CheckConnections();
        var admin2 = r.Login(); var review = r.Service.ReviewRecovery(admin2.Token);
        await r.Service.DispatchNextAsync();
        Rig.Reject("review_stale", () => r.Service.ApproveRecovery(admin2.Token, review.ReviewId));
        var fresh = r.Service.ReviewRecovery(admin2.Token);
        Assert.Equal(LeaseMode.Free, r.Service.ApproveRecovery(admin2.Token, fresh.ReviewId).Mode);
    }
}
