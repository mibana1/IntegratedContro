using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class LogoutTests
{
    [Fact]
    public void Logout_revokes_only_its_session_and_allows_another_session_of_the_same_account()
    {
        using var r = new Rig(); r.Device();
        var other = r.Login();
        Assert.True(r.Service.Logout(r.Admin.Token));

        Rig.Reject("unauthorized", () => r.Service.GetState(r.Admin.Token));
        Rig.Reject("unauthorized", () => r.Service.Acquire(r.Admin.Token));
        Rig.Reject("unauthorized", () => r.Service.Submit(r.Admin.Token, r.Manual()));
        Rig.Reject("unauthorized", () => r.Service.Logout(r.Admin.Token));
        Assert.Equal(LeaseMode.Free, r.Service.GetState(other.Token).Lease.Mode);
        var lease = r.Service.Acquire(other.Token);
        Assert.Equal(other.Session.Id, lease.SessionId);
        Assert.Equal(lease.Generation, r.Service.Heartbeat(other.Token, lease.Generation).Generation);
    }

    [Fact]
    public void Logged_out_administrator_cannot_review_or_approve_the_next_operators_recovery()
    {
        using var r = new Rig();
        var next = r.Operator("next");
        r.Service.Logout(r.Admin.Token);
        r.Service.Acquire(next.Token);
        r.Clock.Advance(16); r.Service.CheckConnections();
        var activeAdmin = r.Login();
        var review = r.Service.ReviewRecovery(activeAdmin.Token);

        Rig.Reject("unauthorized", () => r.Service.ReviewRecovery(r.Admin.Token));
        Rig.Reject("unauthorized", () => r.Service.ApproveRecovery(r.Admin.Token, review.ReviewId));
        Assert.Equal(LeaseMode.RecoveryRequired, r.Service.GetState(activeAdmin.Token).Lease.Mode);
        Assert.Equal(LeaseMode.Free, r.Service.ApproveRecovery(activeAdmin.Token, review.ReviewId).Mode);
    }

    [Fact]
    public void Logout_prevents_using_a_review_created_before_logout()
    {
        using var r = new Rig();
        r.Clock.Advance(16); r.Service.CheckConnections();
        var reviewer = r.Login();
        var review = r.Service.ReviewRecovery(reviewer.Token);
        r.Service.Logout(reviewer.Token);

        Rig.Reject("unauthorized", () => r.Service.ApproveRecovery(reviewer.Token, review.ReviewId));
        var activeAdmin = r.Login();
        Assert.Equal(LeaseMode.RecoveryRequired, r.Service.GetState(activeAdmin.Token).Lease.Mode);
    }

    [Fact]
    public void Viewer_logout_keeps_the_current_operators_lease()
    {
        using var r = new Rig();
        var viewer = r.Operator("viewer", AccountRole.Viewer);
        r.Service.Logout(viewer.Token);

        Rig.Reject("unauthorized", () => r.Service.GetState(viewer.Token));
        var lease = r.Service.Heartbeat(r.Admin.Token, r.Generation);
        Assert.Equal(r.Admin.Session.Id, lease.SessionId);
        Assert.Equal(r.Generation, lease.Generation);
    }

    [Fact]
    public async Task Logout_and_submit_race_preserves_only_requests_accepted_before_logout()
    {
        using var r = new Rig(); r.Device();
        var observer = r.Login();
        var owner = r.Admin;
        for (var i = 0; i < 20; i++)
        {
            var request = r.Manual();
            var token = owner.Token;
            Job? accepted = null;
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logout = Task.Run(async () => { await go.Task; r.Service.Logout(token); });
            var submit = Task.Run(async () =>
            {
                await go.Task;
                try { accepted = r.Service.Submit(token, request); }
                catch (DomainException e) { Assert.Equal("unauthorized", e.Code); }
            });
            go.SetResult(); await Task.WhenAll(logout, submit);

            Rig.Reject("unauthorized", () => r.Service.GetState(token));
            var state = r.Service.GetState(observer.Token);
            var jobs = state.Jobs.Where(j => j.Snapshot.RequestId == request.RequestId).ToArray();
            if (accepted is null) Assert.Empty(jobs);
            else
            {
                Assert.Equal(accepted.Id, Assert.Single(jobs).Id);
                Assert.True(await r.Service.DispatchNextAsync());
                var completed = r.Service.GetState(observer.Token).Jobs.Single(j => j.Id == accepted.Id);
                Assert.Equal(JobStatus.Completed, completed.Status);
                Assert.Equal(owner.Session.Id, completed.Snapshot.SessionId);
            }
            owner = r.Login(); r.Generation = r.Service.Acquire(owner.Token).Generation;
        }
    }
}
