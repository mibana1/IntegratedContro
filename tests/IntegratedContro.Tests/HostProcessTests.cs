using System.Net;
using System.Net.Http.Json;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class HostProcessTests
{
    private static async Task WaitForSimulatedValue(string directory, DeviceConfig device, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "control.sqlite"), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT value FROM virtual_values WHERE pc_id=$pc AND device_id=$device AND operation='Brightness'";
            query.Parameters.AddWithValue("$pc", device.PcId.ToString()); query.Parameters.AddWithValue("$device", device.Id.ToString());
            if (await query.ExecuteScalarAsync() is long value && value == expected) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Simulator did not reach the expected value before crash injection");
    }
    [Fact]
    public async Task Real_https_host_two_local_clients_handoff_disconnect_and_crash_recovery()
    {
        await using var host = new HostProcess(); await host.Initialize(3);
        using (var anonymous = host.NewClient()) Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
        var (a, aLogin) = await host.Login();
        var lease = await HostProcess.Post<Lease>(a, "/api/lease/acquire");
        var device = await HostProcess.Post<DeviceConfig>(a, "/api/devices", new DeviceRequest(lease.Generation,
            Guid.NewGuid(), Guid.NewGuid(), Environment.MachineName, "가상 조명", "virtual-connection", "virtual-light"));
        await HostProcess.Post<RoleBinding>(a, "/api/roles", new RoleRequest(lease.Generation, "light", device.Id));
        await HostProcess.Post<AccountView>(a, "/api/accounts", new CreateAccountRequest(lease.Generation, "operator", host.Password, AccountRole.Operator));
        var (b, bLogin) = await host.Login("operator");
        await HostProcess.Post<Lease>(a, "/api/lease/release", new LeaseRequest(lease.Generation));
        var race = await Task.WhenAll(a.PostAsync("/api/lease/acquire", null), b.PostAsync("/api/lease/acquire", null));
        Assert.Single(race, response => response.IsSuccessStatusCode);
        var winner = race[0].IsSuccessStatusCode ? a : b;
        var winnerLease = (await HostProcess.State(a)).Lease;
        await HostProcess.Post<Lease>(winner, "/api/lease/release", new LeaseRequest(winnerLease.Generation));
        foreach (var response in race) response.Dispose();
        lease = await HostProcess.Post<Lease>(a, "/api/lease/acquire");
        var request = new SubmitRequest(Guid.NewGuid(), lease.Generation, "light", DeviceOperation.Brightness, 37, DelayBeforeMs: 1000);
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => HostProcess.Post<Job>(a, "/api/jobs", request)));
        Assert.Single(duplicates.Select(j => j.Id).Distinct());
        var cancelRequest = request with { RequestId = Guid.NewGuid(), Value = 91, DelayBeforeMs = 60000 };
        var cancelledJob = await HostProcess.Post<Job>(a, "/api/jobs", cancelRequest);
        await HostProcess.Post<Lease>(a, "/api/lease/release", new LeaseRequest(lease.Generation));
        var bLease = await HostProcess.Post<Lease>(b, "/api/lease/acquire");
        await HostProcess.Post<Job>(b, "/api/jobs/cancel", new JobActionRequest(bLease.Generation, cancelledJob.Id));
        using (var stale = await a.PostAsJsonAsync("/api/jobs", request with { RequestId = Guid.NewGuid() }, JsonDefaults.Options))
            Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        var state = await HostProcess.Until(b, s => s.Jobs.Single(j => j.Id == duplicates[0].Id).Status == JobStatus.Completed);
        Assert.Equal(aLogin.Session.UserId, state.Jobs.Single(j => j.Id == cancelledJob.Id).Snapshot.RequestedBy);
        Assert.Equal(bLogin.Session.UserId, state.Jobs.Single(j => j.Id == cancelledJob.Id).CancelledBy);
        Assert.Equal(37, state.DeviceStates[device.Id].Simulated[DeviceOperation.Brightness].Value);
        // No heartbeat from B: the host must fence, retain jobs, and require administrator review.
        state = await HostProcess.Until(a, s => s.Lease.Mode == LeaseMode.RecoveryRequired);
        Assert.NotNull(state.Lease.FencedAt);
        using (var late = await b.PostAsJsonAsync("/api/jobs", new SubmitRequest(Guid.NewGuid(), bLease.Generation, "light"), JsonDefaults.Options))
            Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        using (var denied = await b.PostAsync("/api/recovery/review", null)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var review = await HostProcess.Post<RecoveryReview>(a, "/api/recovery/review");
        await HostProcess.Post<Lease>(a, "/api/recovery/approve", new RecoveryApprovalRequest(review.ReviewId));
        lease = await HostProcess.Post<Lease>(a, "/api/lease/acquire");
        // Losing all client connections must not stop the separate EXE or its already accepted work.
        var unattended = await HostProcess.Post<Job>(a, "/api/jobs", request with { RequestId = Guid.NewGuid(), Generation = lease.Generation, Value = 48, DelayBeforeMs = 500 });
        await HostProcess.Post<bool>(a, "/api/logout"); a.Dispose(); b.Dispose();
        await Task.Delay(900);
        Assert.False(host.Process!.HasExited);
        var (observer, _) = await host.Login();
        state = await HostProcess.Until(observer, s => s.Jobs.Single(j => j.Id == unattended.Id).Status == JobStatus.Completed);
        lease = await HostProcess.Post<Lease>(observer, "/api/lease/acquire");
        device = await HostProcess.Post<DeviceConfig>(observer, "/api/devices", new DeviceRequest(lease.Generation,
            device.Id, device.PcId, device.PcName, device.Name, device.ConnectionId, device.ModelId,
            Fault: VirtualFault.ResponseLost, ExpectedVersion: device.Version));
        var unknown = await HostProcess.Post<Job>(observer, "/api/jobs", new SubmitRequest(Guid.NewGuid(), lease.Generation,
            "light", DeviceOperation.Brightness, 63, TimeoutMs: 30000));
        await HostProcess.Until(observer, s => s.Jobs.Single(j => j.Id == unknown.Id).Steps[0].Status == StepStatus.Dispatching);
        await WaitForSimulatedValue(host.DataPath, device, 63); // Confirm only the isolated simulator's state before killing the process.
        await host.Kill(); await host.Run();
        var (restarted, _) = await host.Login();
        state = await HostProcess.State(restarted);
        Assert.Equal(JobStatus.NeedsReview, state.Jobs.Single(j => j.Id == unknown.Id).Status);
        Assert.Equal(StepStatus.Unknown, state.Jobs.Single(j => j.Id == unknown.Id).Steps[0].Status);
        Assert.Contains(device.Id, state.UncertainDevices);
        Assert.Equal(LeaseMode.RecoveryRequired, state.Lease.Mode);
        using (var oldToken = await observer.GetAsync("/api/state")) Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        review = await HostProcess.Post<RecoveryReview>(restarted, "/api/recovery/review");
        await HostProcess.Post<Lease>(restarted, "/api/recovery/approve", new RecoveryApprovalRequest(review.ReviewId));
        lease = await HostProcess.Post<Lease>(restarted, "/api/lease/acquire");
        var reconciled = await HostProcess.Post<DeviceState>(restarted, "/api/devices/reconcile", new ReconcileRequest(lease.Generation, device.Id));
        Assert.Equal(63, reconciled.Simulated[DeviceOperation.Brightness].Value);
        state = await HostProcess.State(restarted);
        Assert.Equal(JobStatus.NeedsReview, state.Jobs.Single(j => j.Id == unknown.Id).Status);
        Assert.Single(state.Audit, x => x.Action == "DispatchIntent" && x.Detail.Contains(unknown.Id.ToString()));
        // The test uses loopback; it deliberately makes no assertion of physical second-PC validation.
    }
}
