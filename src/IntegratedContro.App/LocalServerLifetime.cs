using System.Diagnostics;
using System.IO;

namespace IntegratedContro.App;

// Own only processes launched by this app instance. Reused servers never enter this list.
public sealed class LocalServerLifetime
{
    private readonly object _gate = new();
    private readonly List<OwnedServer> _servers = [];
    private bool _stopping;
    private Task? _stopTask;
    private sealed record OwnedServer(Process Process, EventWaitHandle? Shutdown);

    internal Process Start(string executable, string[] arguments, string directory)
    {
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException("앱 종료 중에는 서버를 시작하지 않습니다.");
            EventWaitHandle? shutdown = null;
            try
            {
                if (Path.GetFileName(executable).Equals("IntegratedContro.ControlHost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var name = @"Local\IntegratedContro.ServerStop." + Guid.NewGuid().ToString("N");
                    shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, name);
                    arguments = [.. arguments, "--shutdown-event", name];
                }
                var process = WindowsServerProcesses.Start(executable, arguments, directory);
                _servers.Add(new(process, shutdown));
                return process;
            }
            catch { shutdown?.Dispose(); throw; }
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return _stopTask ??= StopOwnedAsync(_servers.AsEnumerable().Reverse().ToArray());
        }
    }

    private static async Task StopOwnedAsync(OwnedServer[] servers)
    {
        var failures = new List<Exception>();
        foreach (var server in servers)
        {
            try
            {
                if (server.Process.HasExited) continue;
                if (server.Shutdown is not null)
                {
                    // ControlHost stops accepting work and runs its existing worker/display cleanup.
                    server.Shutdown.Set();
                    try { await server.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (TimeoutException) { }
                }
                if (!server.Process.HasExited)
                {
                    server.Process.Kill();
                    await server.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException) { /* Process already exited. */ }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or TimeoutException) { failures.Add(error); }
            finally { server.Shutdown?.Dispose(); server.Process.Dispose(); }
        }
        if (failures.Count > 0) throw new AggregateException("일부 서버를 종료하지 못했습니다.", failures);
    }
}
