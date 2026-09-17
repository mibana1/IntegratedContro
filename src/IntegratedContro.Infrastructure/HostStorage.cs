using IntegratedContro.Application;

namespace IntegratedContro.Infrastructure;

// One storage lifetime; SQL connection details do not escape into the process entry point.
public sealed class HostStorage(string dataPath, IStateStore state, IVirtualDeviceTransport virtualDevices) : IDisposable
{
    public string DataPath { get; } = dataPath;
    public IStateStore State { get; } = state;
    public IVirtualDeviceTransport VirtualDevices { get; } = virtualDevices;
    public void Dispose() => State.Dispose();
}

public static class SqliteHostStorage
{
    public static HostStorage Open(string dataPath, bool initialize = false)
    {
        var state = new SqliteStateStore(dataPath, initialize);
        return new(state.DataPath, state, new SqliteVirtualDeviceTransport(state.ConnectionString));
    }
}
