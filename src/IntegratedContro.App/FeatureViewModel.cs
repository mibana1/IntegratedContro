using System.Collections.ObjectModel;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

// The shell supplies one consistent observation plus session/command availability.
// Features never own credentials, polling, or pending-request retry state.
public sealed record FeatureContext(StateView? State = null, bool Connected = false,
    bool CanControl = false, bool CanConfigure = false, bool Busy = false,
    bool Closing = false, bool HasPending = false);

public interface IFeatureSession
{
    Task RunAsync(Func<Task> action);
    void ReportStatus(string message);
    Task SubmitAsync(SubmitRequest request);
}

public interface ILightingHost : IFeatureSession
{
    Task<LightLayout> SaveLayoutAsync(LightOrderRequest request);
    Task<DeviceState> ReconcileAsync(ReconcileRequest request);
    Task SubmitBatchAsync(LightBatchRequest request);
}

public interface IScenarioHost : IFeatureSession
{
    Task<ScenarioDefinition> SaveAsync(ScenarioRequest request);
    Task DeleteAsync(DeleteScenarioRequest request);
}

public abstract class FeatureViewModel(IFeatureSession session) : Bindable
{
    private readonly List<AsyncCommand> _commands = [];
    protected FeatureContext Context { get; private set; } = new();
    protected StateView? State => Context.State;
    public bool CanControl => Context.CanControl;
    public bool CanConfigure => Context.CanConfigure;
    public bool IsLoggedIn => State is not null;
    public bool IsAdmin => Context.Connected && State?.Session.Role == AccountRole.Administrator;
    protected bool HasPending => Context.HasPending;
    protected long Generation => State?.Lease.Generation ?? 0;
    protected void ReportStatus(string message) => session.ReportStatus(message);

    public void UpdateContext(FeatureContext context)
    {
        var previous = Context;
        Context = context;
        OnContextChanged(previous);
        Changed(nameof(CanControl)); Changed(nameof(CanConfigure)); Changed(nameof(IsLoggedIn)); Changed(nameof(IsAdmin));
        RefreshCommands();
    }

    protected abstract void OnContextChanged(FeatureContext previous);

    protected AsyncCommand Command(Func<Task> action, Func<bool>? available = null, bool register = true)
    {
        var command = new AsyncCommand(async () =>
        {
            try { await session.RunAsync(action); }
            finally { UpdateContext(Context); }
        },
            () => !Context.Busy && !Context.Closing && (available?.Invoke() ?? true));
        if (register) _commands.Add(command);
        return command;
    }

    protected AsyncCommand LocalCommand(Action action, Func<bool> available)
    {
        var command = new AsyncCommand(() => { action(); return Task.CompletedTask; },
            () => !Context.Busy && !Context.Closing && available());
        _commands.Add(command);
        return command;
    }

    protected void RefreshCommands()
    {
        foreach (var command in _commands) command.Raise();
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var list = values.ToArray();
        if (JsonSerializer.Serialize(target, JsonDefaults.Options) == JsonSerializer.Serialize(list, JsonDefaults.Options)) return;
        target.Clear();
        foreach (var value in list) target.Add(value);
    }
}
