using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class SnapshotIsolationTests
{
    [Fact]
    public void Caller_owned_scenario_array_cannot_change_saved_definition_or_execution_snapshot()
    {
        using var r = new Rig(); r.Device();
        ScenarioStep[] input = [new("light", DeviceOperation.Power, 1)];
        var definition = r.Scenario(input);
        input[0] = input[0] with { Value = 0 };
        var job = r.SubmitScenario(definition);
        Assert.Equal(1, job.Snapshot.Steps[0].Value);
        Assert.Equal(1, r.Service.GetState(r.Admin.Token).Scenarios.Single().Steps[0].Value);
    }
}
