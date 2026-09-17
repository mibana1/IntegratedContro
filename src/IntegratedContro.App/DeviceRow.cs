using System.Collections.ObjectModel;
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
    public string Label => $"{Name} / {PcName} · {Id.ToString()[..8]}";
    public void Update(DeviceRow current)
    {
        Config = current.Config; State = current.State; Desired = current.Desired;
        Connection = current.Connection; Result = current.Result; Restriction = current.Restriction;
        foreach (var name in new[] { nameof(Config), nameof(Name), nameof(PcName), nameof(Model), nameof(Label),
            nameof(State), nameof(Desired), nameof(Connection), nameof(Result), nameof(Restriction) }) Changed(name);
    }
    public Guid Id => Config.Id;
    public string Name => Config.Name;
    public string PcName => Config.PcName;
    public string Model => Config.ModelId;
}
