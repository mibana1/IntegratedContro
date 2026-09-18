using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class DeviceRow(DeviceConfig config, string state, string desired, string connection, string result, string restriction) : Bindable
{
    public DeviceConfig Config { get; private set; } = config;
    public string State { get; private set; } = state;
    public string Desired { get; private set; } = desired;
    public string Connection { get; private set; } = connection;
    public string Result { get; private set; } = result;
    public string Restriction { get; private set; } = restriction;
    public string Label => $"{Name}" + (Config.Location.Length > 0 ? $" · {Config.Location}" : "") +
        (Config.Connection.Endpoint.Length > 0 ? $" · {Config.Connection.Endpoint}" : "");
    public bool IsSimulation { get; set; }
    public string Diagnostics => IsSimulation && Config.Fault != VirtualFault.None ? $"장애 주입 중: {Config.Fault} · 지연 {Config.LatencyMs}ms" : "";
    public void Update(DeviceRow current)
    {
        Config = current.Config; IsSimulation = current.IsSimulation; State = current.State; Desired = current.Desired;
        Connection = current.Connection; Result = current.Result; Restriction = current.Restriction;
        foreach (var name in new[] { nameof(Config), nameof(Name), nameof(PcName), nameof(Label),
            nameof(Diagnostics), nameof(State), nameof(Desired), nameof(Connection), nameof(Result), nameof(Restriction) }) Changed(name);
    }
    public Guid Id => Config.Id;
    public string Name => Config.Name;
    public string PcName => Config.PcName;
}
