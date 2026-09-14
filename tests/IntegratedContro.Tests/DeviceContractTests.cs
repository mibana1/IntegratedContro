using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class DeviceContractTests
{
    private sealed class EvidenceDriver : IDeviceDriver
    {
        public Capability Power { get; set; } = new(DeviceOperation.Power, 0, 1, "on/off");
        public DeviceModel[] Models => [new("evidence", "검증용 어댑터", [Power, new(DeviceOperation.Stop, 0, 0, "STOP") { CanRead = false }])];
        public List<StepSnapshot> Sent { get; } = [];
        public DriverResult Result { get; set; } = new(StepStatus.Succeeded, "ACK") { Confirmation = ConfirmationLevel.ProtocolAcknowledged };
        public DriverReading Reading { get; set; } = new(true, new Dictionary<DeviceOperation, int>(), "관측") { Confirmation = ConfirmationLevel.Observed };
        public Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken ct) { Sent.Add(step); return Task.FromResult(Result); }
        public Action? OnRead { get; set; }
        public Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken ct) { OnRead?.Invoke(); return Task.FromResult(Reading); }
    }
    private sealed class Fixture : IDisposable
    {
        public Rig Rig { get; } = new();
        public EvidenceDriver Driver { get; } = new();
        public ControlService Service { get; }
        public LoginResult Login { get; }
        public long Generation { get; }
        public DeviceConfig Device { get; }
        public Fixture()
        {
            Service = new(Rig.Store, Rig.Hasher, Driver, Rig.Clock);
            Login = Service.Login(new("admin", Rig.Password, Guid.NewGuid(), "test PC"));
            Service.ApproveRecovery(Login.Token, Service.ReviewRecovery(Login.Token).ReviewId);
            Generation = Service.Acquire(Login.Token).Generation;
            Device = AddDevice("light");
        }
        public DeviceConfig AddDevice(string role)
        {
            var target = Service.SaveDevice(Login.Token, new(Generation, Guid.NewGuid(), Guid.NewGuid(), "test PC", role, "shared", "evidence"));
            Service.SaveRole(Login.Token, new(Generation, role, target.Id)); return target;
        }
        public Job Submit(string role = "light", DeviceOperation op = DeviceOperation.Power) =>
            Service.Submit(Login.Token, new(Guid.NewGuid(), Generation, role, op, op == DeviceOperation.Stop ? 0 : 1));
        public StateView State => Service.GetState(Login.Token);
        public void Dispose() => Rig.Dispose();
    }
    [Theory]
    [InlineData(ConfirmationLevel.TransportSent)]
    [InlineData(ConfirmationLevel.ProtocolAcknowledged)]
    public async Task Transport_or_ACK_never_becomes_observed_or_simulated_state(ConfirmationLevel confirmation)
    {
        using var f = new Fixture();
        f.Driver.Result = new(StepStatus.Succeeded, "accepted", new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 1 })
            { Confirmation = confirmation };
        var job = f.Submit(); await f.Service.DispatchNextAsync();
        var state = f.State.DeviceStates[f.Device.Id];
        Assert.Empty(state.Observed); Assert.Empty(state.Simulated);
        Assert.Equal(confirmation, state.LastCommand!.Confirmation);
        Assert.Equal(1, state.Desired[DeviceOperation.Power]);
        var result = f.State.Jobs.Single(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Completed, result.Status);
        Assert.Equal(confirmation, result.Steps.Single().Evidence!.Confirmation);
        Assert.Contains("동작 미확인", DeviceEvidence.Label(confirmation));
    }
    [Fact]
    public async Task Actual_observation_preserves_timestamp_unit_and_expires_without_changing_history()
    {
        using var f = new Fixture(); var at = f.Rig.Clock.GetUtcNow();
        f.Driver.Result = new(StepStatus.Succeeded, "observed")
        {
            Confirmation = ConfirmationLevel.Observed,
            Observations = new Dictionary<DeviceOperation, DeviceObservation> { [DeviceOperation.Power] = new(1, "on/off", at, at.AddHours(1)) }
        };
        f.Submit(); await f.Service.DispatchNextAsync();
        var observation = f.State.DeviceStates[f.Device.Id].Observed[DeviceOperation.Power];
        Assert.Equal(at, observation.At); Assert.Equal("on/off", observation.Unit);
        Assert.Equal(at.AddSeconds(5), observation.ValidUntil); // Host caps driver freshness by capability.
        Assert.True(observation.IsFresh(at));
        f.Rig.Clock.Advance(6);
        Assert.False(f.State.DeviceStates[f.Device.Id].Observed[DeviceOperation.Power].IsFresh(f.Rig.Clock.GetUtcNow()));
        Assert.Contains("오래된 관측", DeviceEvidence.Observations(f.State.DeviceStates[f.Device.Id], f.Rig.Clock.GetUtcNow()));
        Assert.Empty(f.State.DeviceStates[f.Device.Id].Simulated);
        var evidence = f.Service.GetJobHistory(f.Login.Token, new()).Items.Single().Value.Steps.Single().Evidence!;
        Assert.Equal(observation, evidence.Observations[DeviceOperation.Power]);
        Assert.Contains("Power=1", DeviceEvidence.RecordedObservations(evidence));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("constraints")]
    [InlineData("observations")]
    public async Task Malformed_driver_results_are_quarantined_without_crashing_the_worker(string part)
    {
        using var f = new Fixture();
        f.Driver.Result = part switch
        {
            "constraints" => f.Driver.Result with { Constraints = null! },
            "observations" => f.Driver.Result with { Observations = null! },
            _ => null!
        };
        var job = f.Submit();
        Assert.True(await f.Service.DispatchNextAsync());
        Assert.Equal(JobStatus.NeedsReview, f.State.Jobs.Single(j => j.Id == job.Id).Status);
        Assert.Contains(f.Device.Id, f.State.UncertainDevices);
        Assert.False(await f.Service.DispatchNextAsync());
        Assert.Single(f.Driver.Sent);
    }
    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("unit")]
    [InlineData("range")]
    [InlineData("missing")]
    public async Task Invalid_observed_evidence_fails_closed_and_never_completes_as_success(string reason)
    {
        using var f = new Fixture(); var at = f.Rig.Clock.GetUtcNow();
        f.Driver.Result = new(StepStatus.Succeeded, "bad reading")
        {
            Confirmation = ConfirmationLevel.Observed,
            Observations = reason == "missing" ? new Dictionary<DeviceOperation, DeviceObservation>() :
                new Dictionary<DeviceOperation, DeviceObservation> { [DeviceOperation.Power] =
                    new(reason == "range" ? 2 : 1, reason == "unit" ? "dB" : "on/off",
                        reason == "future" ? at.AddSeconds(1) : at.AddSeconds(-1),
                        reason == "stale" ? at.AddMilliseconds(-1) : at.AddSeconds(4)) }
        };
        var job = f.Submit(); await f.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.NeedsReview, f.State.Jobs.Single(j => j.Id == job.Id).Status);
        Assert.Empty(f.State.DeviceStates[f.Device.Id].Observed);
        Assert.Single(f.Driver.Sent);
        Assert.False(await f.Service.DispatchNextAsync());
    }
    [Theory]
    [InlineData(ConfirmationLevel.ProtocolAcknowledged)]
    [InlineData(ConfirmationLevel.Observed)]
    public async Task Condition_and_reconciliation_require_fresh_actual_observation(ConfirmationLevel level)
    {
        using var f = new Fixture(); var at = f.Rig.Clock.GetUtcNow();
        f.Driver.Reading = new(true, new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 1 }, "stale")
        {
            Confirmation = level,
            Observations = new Dictionary<DeviceOperation, DeviceObservation> { [DeviceOperation.Power] = new(1, "on/off", at.AddSeconds(-10), at.AddSeconds(-1)) }
        };
        Assert.Equal("read_unconfirmed", (await Assert.ThrowsAsync<DomainException>(() =>
            f.Service.ReconcileAsync(f.Login.Token, new(f.Generation, f.Device.Id)))).Code);
        var definition = f.Service.SaveScenario(f.Login.Token, new(f.Generation, Guid.NewGuid(), "조건 검증",
            [new("light", DeviceOperation.Power, 1, ConditionOperation: DeviceOperation.Power, ConditionValue: 1)]));
        f.Service.Submit(f.Login.Token, new(Guid.NewGuid(), f.Generation, null, ScenarioId: definition.Id));
        await f.Service.DispatchNextAsync();
        Assert.Empty(f.Driver.Sent);
    }
    [Fact]
    public void Unsupported_read_condition_is_rejected_before_acceptance()
    {
        using var f = new Fixture(); f.Driver.Power = f.Driver.Power with { CanRead = false };
        Rig.Reject("invalid_condition", () => f.Service.SaveScenario(f.Login.Token, new(f.Generation, Guid.NewGuid(), "불가 조건",
            [new("light", DeviceOperation.Power, 1, ConditionOperation: DeviceOperation.Power, ConditionValue: 1)])));
    }
    [Fact]
    public async Task Shared_connection_interval_and_settling_are_persisted_and_delay_without_interrupting()
    {
        using var f = new Fixture(); f.AddDevice("other");
        f.Driver.Power = f.Driver.Power with { MinimumCommandIntervalMs = 2000, SettleAfterMs = 3000 };
        var first = f.Submit(); var second = f.Submit("other");
        await f.Service.DispatchNextAsync(); await f.Service.DispatchNextAsync();
        Assert.Single(f.Driver.Sent);
        Assert.Equal(JobStatus.Queued, f.State.Jobs.Single(j => j.Id == second.Id).Status);
        Assert.Equal(f.Rig.Clock.GetUtcNow().AddSeconds(2), f.Rig.Store.Load().ConnectionNotBefore["shared"]);
        f.Rig.Clock.Advance(2); await f.Service.DispatchNextAsync();
        Assert.Equal(2, f.Driver.Sent.Count);
        Assert.Equal(JobStatus.Completed, f.State.Jobs.Single(j => j.Id == first.Id).Status);
    }
    [Fact]
    public async Task Slow_condition_read_does_not_consume_the_actual_command_interval()
    {
        using var f = new Fixture(); f.AddDevice("other");
        f.Driver.Power = f.Driver.Power with { MinimumCommandIntervalMs = 2000 };
        f.Driver.Reading = new(true, new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = 1 }, "가상 조건");
        f.Driver.OnRead = () => f.Rig.Clock.Advance(5);
        var definition = f.Service.SaveScenario(f.Login.Token, new(f.Generation, Guid.NewGuid(), "느린 조건 조회",
            [new("light", DeviceOperation.Power, 1, ConditionOperation: DeviceOperation.Power, ConditionValue: 1)]));
        f.Service.Submit(f.Login.Token, new(Guid.NewGuid(), f.Generation, null, ScenarioId: definition.Id));
        var waiting = f.Submit("other");
        await f.Service.DispatchNextAsync(); await f.Service.DispatchNextAsync();
        Assert.Single(f.Driver.Sent);
        Assert.Equal(JobStatus.Queued, f.State.Jobs.Single(j => j.Id == waiting.Id).Status);
        Assert.Equal(f.Rig.Clock.GetUtcNow().AddSeconds(2), f.Rig.Store.Load().ConnectionNotBefore["shared"]);
        f.Rig.Clock.Advance(2); await f.Service.DispatchNextAsync();
        Assert.Equal(2, f.Driver.Sent.Count);
    }
    [Fact]
    public async Task Changed_condition_capability_blocks_accepted_work_even_if_command_contract_is_unchanged()
    {
        using var f = new Fixture();
        var definition = f.Service.SaveScenario(f.Login.Token, new(f.Generation, Guid.NewGuid(), "조건 고정",
            [new("light", DeviceOperation.Stop, 0, ConditionOperation: DeviceOperation.Power, ConditionValue: 1)]));
        var job = f.Service.Submit(f.Login.Token, new(Guid.NewGuid(), f.Generation, null, ScenarioId: definition.Id));
        Assert.Equal(f.Driver.Power, job.Snapshot.Steps.Single().ConditionCapability);
        f.Driver.Power = f.Driver.Power with { ObservationMaxAgeMs = 1000 };
        await f.Service.DispatchNextAsync();
        Assert.Empty(f.Driver.Sent);
        Assert.Equal(JobStatus.Interrupted, f.State.Jobs.Single(j => j.Id == job.Id).Status);
        Assert.Contains("관측 제약 변경", f.State.Jobs.Single(j => j.Id == job.Id).Result);
    }
    [Fact]
    public async Task Settling_does_not_block_supported_STOP_and_does_not_auto_retry()
    {
        using var f = new Fixture(); f.Driver.Power = f.Driver.Power with { SettleAfterMs = 10000 };
        f.Submit(); await f.Service.DispatchNextAsync();
        f.Submit(op: DeviceOperation.Stop); await f.Service.DispatchNextAsync();
        Assert.Equal(DeviceOperation.Stop, f.Driver.Sent.Last().Operation);
        f.Submit(); await f.Service.DispatchNextAsync();
        Assert.Equal(2, f.Driver.Sent.Count);
        f.Rig.Clock.Advance(10); await f.Service.DispatchNextAsync();
        Assert.Equal(3, f.Driver.Sent.Count);
    }
}
