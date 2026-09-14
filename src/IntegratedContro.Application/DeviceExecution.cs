using IntegratedContro.Core;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private Capability CapabilityFor(StepSnapshot step) => step.Capability ??
        _driver.Models.Single(m => m.Id == step.Target!.ModelId).Capabilities.Single(c => c.Operation == step.Operation);

    private DateTimeOffset DispatchNotBefore(HostState state, StepSnapshot step)
    {
        var constraints = state.DeviceStates[step.Target!.Id].Constraints.Where(c => c.Blocks(step.Operation, Now))
            .Select(c => c.NotBefore);
        var lastDispatch = state.ConnectionLastDispatchAt.GetValueOrDefault(step.Target!.ConnectionId);
        return constraints.Append(state.ConnectionNotBefore.GetValueOrDefault(step.Target!.ConnectionId))
            .Append(lastDispatch.AddMilliseconds(CapabilityFor(step).MinimumCommandIntervalMs)).Max();
    }
    private Dictionary<DeviceOperation, DeviceObservation> ValidObservations(DeviceConfig target,
        IReadOnlyDictionary<DeviceOperation, DeviceObservation> observations)
    {
        var capabilities = _driver.Models.Single(m => m.Id == target.ModelId).Capabilities;
        var valid = new Dictionary<DeviceOperation, DeviceObservation>();
        foreach (var pair in observations)
        {
            var capability = capabilities.SingleOrDefault(c => c.Operation == pair.Key && c.CanRead);
            var value = pair.Value;
            if (capability is null || value is null || !double.IsFinite(value.Value) ||
                value.Value < capability.Minimum || value.Value > capability.Maximum || value.Unit != capability.Unit ||
                value.At > Now || value.ValidUntil <= value.At) continue;
            var until = value.At.AddMilliseconds(capability.ObservationMaxAgeMs);
            valid[pair.Key] = value with { ValidUntil = value.ValidUntil < until ? value.ValidUntil : until };
        }
        return valid;
    }
    private bool ConditionSatisfied(StepSnapshot step, DriverReading reading)
    {
        if (!reading.Available) return false;
        if (reading.Confirmation == ConfirmationLevel.Simulated)
            return reading.Values.TryGetValue(step.ConditionOperation!.Value, out var value) && value == step.ConditionValue;
        if (reading.Confirmation != ConfirmationLevel.Observed) return false;
        return ValidObservations(step.Target!, reading.Observations).TryGetValue(step.ConditionOperation!.Value, out var observed) &&
            observed.IsFresh(Now) && observed.Value == step.ConditionValue;
    }
    private DriverResult ValidateResult(StepSnapshot step, DriverResult? result)
    {
        if (result is null || result.Observations is null || result.Constraints is null || string.IsNullOrWhiteSpace(result.Detail))
            return new(StepStatus.Unknown, "드라이버 결과 누락: 상태 대조 필요");
        var success = result.Status is StepStatus.Simulated or StepStatus.Succeeded;
        var valid = Enum.IsDefined(result.Status) && Enum.IsDefined(result.Outcome) && Enum.IsDefined(result.Confirmation) &&
            result.Status is not (StepStatus.Pending or StepStatus.Dispatching) &&
            (result.Status switch
            {
                StepStatus.Simulated or StepStatus.Succeeded => result.Outcome == CommandOutcome.Succeeded,
                StepStatus.Failed => result.Outcome is CommandOutcome.Failed or CommandOutcome.Unsupported,
                StepStatus.Skipped => result.Outcome == CommandOutcome.Cancelled,
                StepStatus.Unknown => result.Outcome is CommandOutcome.Unknown or CommandOutcome.TimedOut or CommandOutcome.Cancelled,
                _ => false
            }) &&
            result.Constraints.All(c => c is not null && c.NotBefore <= Now.AddHours(24) &&
                (c.Operation is null || Enum.IsDefined(c.Operation.Value)) && !string.IsNullOrWhiteSpace(c.Reason)) &&
            (result.Status != StepStatus.Simulated || result.Confirmation == ConfirmationLevel.Simulated) &&
            (result.Status != StepStatus.Succeeded || result.Confirmation is ConfirmationLevel.TransportSent or ConfirmationLevel.ProtocolAcknowledged or ConfirmationLevel.Observed);
        if (result.Confirmation == ConfirmationLevel.Observed)
            valid &= ValidObservations(step.Target!, result.Observations).TryGetValue(step.Operation, out var value) &&
                value.IsFresh(Now) && (!success || value.Value == step.Value);
        if (!valid) return new(StepStatus.Unknown, "드라이버 결과·확인 근거 불일치: 상태 대조 필요");
        return result;
    }
    private DeviceCommandResult EvidenceFor(StepSnapshot step, DriverResult result) =>
        new(result.Outcome, result.Confirmation, result.Detail, Now)
        { Observations = result.Confirmation == ConfirmationLevel.Observed ? ValidObservations(step.Target!, result.Observations) : [] };
    private void ApplyDriverState(DeviceState state, StepSnapshot step, DriverResult result)
    {
        state.LastCommand = EvidenceFor(step, result);
        state.LastResult = result.Detail;
        state.ConnectionStatus = result.Status is StepStatus.Simulated or StepStatus.Succeeded
            ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.RecoveryRequired;
        state.Connection = result.Status == StepStatus.Simulated ? "가상 연결됨" :
            result.Status == StepStatus.Succeeded ? DeviceEvidence.Label(result.Confirmation) : "장비 오류/대조 필요";
        if (result.Confirmation == ConfirmationLevel.Simulated && result.Values is not null)
            foreach (var pair in result.Values) state.Simulated[pair.Key] = new(pair.Value, Now);
        if (result.Confirmation == ConfirmationLevel.Observed)
            foreach (var pair in ValidObservations(step.Target!, result.Observations)) state.Observed[pair.Key] = pair.Value;
        state.Constraints.RemoveAll(c => c.NotBefore <= Now);
        state.Constraints.AddRange(result.Constraints.Where(c => c.NotBefore > Now &&
            c.NotBefore <= Now.AddHours(24) && (c.Operation is null || Enum.IsDefined(c.Operation.Value))));
        if (result.Outcome == CommandOutcome.Succeeded && CapabilityFor(step).SettleAfterMs is > 0 and var settle)
            state.Constraints.Add(new(null, Now.AddMilliseconds(settle), "장비 안정화 대기"));
    }
}
