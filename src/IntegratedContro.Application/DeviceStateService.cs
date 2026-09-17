using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class DeviceExecutionService
{
    private bool HasStateEvidence(DeviceConfig target, DeviceEvidence evidence) =>
        _drivers.Model(target.ModelId).IsSimulation ? evidence == DeviceEvidence.Simulation : evidence == DeviceEvidence.Observed;

    public bool ValidReading(DeviceConfig target, DriverReading reading) => reading.Available &&
        HasStateEvidence(target, reading.Evidence) && ValidValues(target, reading.Values);

    private bool ValidValues(DeviceConfig target, IReadOnlyDictionary<DeviceOperation, int> values)
    {
        var model = _drivers.Model(target.ModelId);
        return values.Count > 0 && values.All(p => model.Capabilities.Any(c => c.Operation == p.Key && c.CanRead &&
            p.Value >= c.Minimum && p.Value <= c.Maximum));
    }

    private void RecordValues(DeviceState state, DeviceConfig target, IReadOnlyDictionary<DeviceOperation, int> values,
        DeviceEvidence evidence, bool replace = false)
    {
        if (!HasStateEvidence(target, evidence) || !ValidValues(target, values)) return;
        var destination = evidence == DeviceEvidence.Simulation ? state.Simulated : state.Observed;
        if (replace) destination.Clear();
        foreach (var pair in values) destination[pair.Key] = new(pair.Value, _host.Now,
            evidence == DeviceEvidence.Simulation ? "가상 상태" : "장비 관측");
    }

    private string ConnectedLabel(DeviceConfig target) => _drivers.Model(target.ModelId).IsSimulation ? "가상 연결됨" : "장비 응답 확인";
}
