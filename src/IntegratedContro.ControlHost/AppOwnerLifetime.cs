using System.Runtime.Versioning;

namespace IntegratedContro.ControlHost;

// Optional ownership signal used only for hosts launched by an app. Standalone hosts retain their lifetime.
[SupportedOSPlatform("windows")]
internal sealed class AppOwnerLifetime : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;
    private AppOwnerLifetime(string name, IHostApplicationLifetime lifetime)
    {
        _signal = EventWaitHandle.OpenExisting(name);
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => lifetime.StopApplication(), null, Timeout.Infinite, true);
    }
    public static AppOwnerLifetime? Listen(string[] args, IHostApplicationLifetime lifetime)
    {
        var name = HostSetup.Option(args, "--shutdown-event");
        if (name is null) return null;
        if (!name.StartsWith(@"Local\IntegratedContro.ServerStop.", StringComparison.Ordinal))
            throw new ArgumentException("앱 종료 신호 설정을 확인하세요.");
        return new(name, lifetime);
    }
    public void Dispose() { _wait.Unregister(null); _signal.Dispose(); }
}
