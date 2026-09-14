namespace IntegratedContro.Core;

public enum ConfirmationLevel { None, Simulated, TransportSent, ProtocolAcknowledged, Observed }
public enum CommandOutcome { None, Succeeded, Failed, TimedOut, Cancelled, Unsupported, Unknown }
public enum DeviceConnectionStatus { NotChecked, Connected, Disconnected, RecoveryRequired, Error }
public enum RetrySafety { Never, IdempotentAfterRevalidation }
public sealed record DeviceObservation(double Value, string Unit, DateTimeOffset At, DateTimeOffset ValidUntil)
{
    public bool IsFresh(DateTimeOffset now) => double.IsFinite(Value) && At <= now && now < ValidUntil;
}
public sealed record DeviceConstraint(DeviceOperation? Operation, DateTimeOffset NotBefore, string Reason)
{
    public bool Blocks(DeviceOperation operation, DateTimeOffset now) =>
        operation != DeviceOperation.Stop && (Operation is null || Operation == operation) && now < NotBefore;
}
public sealed record DeviceCommandResult(CommandOutcome Outcome, ConfirmationLevel Confirmation,
    string Detail, DateTimeOffset At)
{
    public Dictionary<DeviceOperation, DeviceObservation> Observations { get; init; } = [];
}
public static class DeviceEvidence
{
    public static string Label(ConfirmationLevel level) => level switch
    {
        ConfirmationLevel.Simulated => "가상 결과 · 실측 아님",
        ConfirmationLevel.TransportSent => "전송 완료 · 동작 미확인",
        ConfirmationLevel.ProtocolAcknowledged => "프로토콜 ACK · 동작 미확인",
        ConfirmationLevel.Observed => "장비 상태 관측",
        _ => "확인 근거 없음"
    };
    public static string RecordedObservations(DeviceCommandResult? result) => result is null ? "" : string.Join(" / ",
        result.Observations.Select(p => $"관측 {p.Key}={p.Value.Value} {p.Value.Unit} ({p.Value.At.ToLocalTime():yyyy-MM-dd HH:mm:ss})"));
    public static string Observations(DeviceState state, DateTimeOffset now) => string.Join(" / ",
        state.Observed.Select(p => $"{p.Key}={p.Value.Value} {p.Value.Unit} · " +
            $"{(p.Value.IsFresh(now) ? "관측" : "오래된 관측 · 재조회 필요")} ({p.Value.At.ToLocalTime():HH:mm:ss})"));
}
