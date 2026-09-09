using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class BoundaryTests
{
    [Fact]
    public async Task Cancel_dispatch_races_have_one_order_and_never_claim_sent_work_was_rolled_back()
    {
        using var r = new Rig(); r.Device();
        for (var i = 0; i < 40; i++)
        {
            var job = r.Service.Submit(r.Admin.Token, r.Manual());
            var before = r.Driver.Sent.Count;
            await Task.WhenAll(Task.Run(() => r.Service.Cancel(r.Admin.Token, new(r.Generation, job.Id))),
                Task.Run(() => r.Service.DispatchNextAsync()));
            var result = r.Job(job.Id);
            Assert.InRange(r.Driver.Sent.Count - before, 0, 1);
            if (r.Driver.Sent.Count == before)
                Assert.Equal(StepStatus.Skipped, result.Steps[0].Status);
            else
                Assert.Equal(StepStatus.Simulated, result.Steps[0].Status);
            Assert.False(result.Active);
        }
    }
    [Fact]
    public async Task Release_submit_race_has_a_durable_acceptance_boundary()
    {
        using var r = new Rig(); r.Device();
        for (var i = 0; i < 30; i++)
        {
            var request = r.Manual();
            Job? accepted = null;
            await Task.WhenAll(Task.Run(() => r.Service.Release(r.Admin.Token, r.Generation)), Task.Run(() =>
            {
                try { accepted = r.Service.Submit(r.Admin.Token, request); }
                catch (DomainException e) { Assert.Equal("lease_required", e.Code); }
            }));
            var jobs = r.Service.GetState(r.Admin.Token).Jobs.Where(j => j.Snapshot.RequestId == request.RequestId).ToArray();
            if (accepted is null) Assert.Empty(jobs);
            else Assert.Equal(accepted.Id, Assert.Single(jobs).Id);
            r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        }
    }
    [Fact]
    public async Task Changing_registered_target_does_not_relabel_old_state_as_new_target_observation()
    {
        using var r = new Rig(); var d = r.Device();
        r.Service.Submit(r.Admin.Token, r.Manual());
        await r.Service.DispatchNextAsync();
        Assert.NotEmpty(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id].Simulated);
        r.Service.SaveDevice(r.Admin.Token, new(r.Generation, d.Id, Guid.NewGuid(), "Explicit-new-PC",
            d.Name, d.ConnectionId, d.ModelId, ExpectedVersion: d.Version));
        Assert.Empty(r.Service.GetState(r.Admin.Token).DeviceStates[d.Id].Simulated);
    }
}
