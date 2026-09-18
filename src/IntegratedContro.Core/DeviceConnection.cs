namespace IntegratedContro.Core;

/// <summary>Connection settings contain no protocol commands or credentials.</summary>
public sealed record DeviceConnection(string TransportId = "virtual", string Endpoint = "", string Address = "")
{
    public Dictionary<string, string> Options { get; init; } = [];
    public Guid? ExecutionPcId { get; init; }

    public bool HasSameSettings(DeviceConnection other) => TransportId == other.TransportId &&
        Endpoint == other.Endpoint && Address == other.Address && ExecutionPcId == other.ExecutionPcId && SameOptions(Options, other.Options);

    public static bool SameOptions(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

public enum DeviceEvidence { Simulation, Sent, Acknowledged, Observed }
