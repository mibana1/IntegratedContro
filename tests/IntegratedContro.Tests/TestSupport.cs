using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.Tests;

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
}
internal sealed class TestHasher : IPasswordHasher
{
    public string Hash(string password) => $"test-hash:{password}";
    public bool Verify(string password, string hash) => hash == Hash(password);
}
internal sealed class GateDriver : IDeviceDriver
{
    public string Id => "virtual";
    public string Version => "1";
    public void ValidateConfiguration(DeviceConfig device) { }
    public DeviceModel[] Models => [new("test", "Test model", [
        new(DeviceOperation.Power,0,1,"on/off"), new(DeviceOperation.Brightness,0,100,"%"),
        new(DeviceOperation.Lift,-1,1,"direction"), new(DeviceOperation.Stop,0,0,"STOP")], DeviceCategory.Lighting)];
    public readonly List<DeviceCommand> Sent = [];
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<DriverResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Hold { get; set; }
    public int ReadPower { get; set; } = 1;
    public bool HoldRead { get; set; }
    public readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource ReadContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DriverStatus NextStatus { get; set; } = DriverStatus.Simulated;
    public async Task<DriverResult> ExecuteAsync(DeviceCommand command, CancellationToken ct)
    {
        Sent.Add(command); Started.TrySetResult();
        if (Hold) return await Completion.Task.WaitAsync(ct);
        return new(NextStatus, "Test virtual result", new Dictionary<DeviceOperation, int> { [command.Operation] = command.Value });
    }
    public async Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken ct)
    {
        ReadStarted.TrySetResult();
        if (HoldRead) await ReadContinue.Task.WaitAsync(ct);
        return new(true, new Dictionary<DeviceOperation, int> { [DeviceOperation.Power] = ReadPower }, "Test virtual reading");
    }
}
internal sealed class Rig : IDisposable
{
    public const string Password = "Local-tests-only-password";
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "IntegratedControTests", Guid.NewGuid().ToString());
    public SqliteStateStore Store { get; private set; }
    public ControlService Service { get; private set; }
    public GateDriver Driver { get; } = new();
    public TestClock Clock { get; } = new();
    public TestHasher Hasher { get; } = new();
    public LoginResult Admin { get; private set; }
    public long Generation { get; set; }
    private readonly IHiperwallReader? _hiperwall;
    private readonly ICredentialStore? _credentials;
    private readonly IMediaMtxClient? _media;
    private readonly IMediaSecretStore? _mediaSecrets;
    public Rig(IHiperwallReader? hiperwall = null, ICredentialStore? credentials = null,
        IMediaMtxClient? media = null, IMediaSecretStore? mediaSecrets = null)
    {
        _hiperwall = hiperwall; _credentials = credentials; _media = media; _mediaSecrets = mediaSecrets;
        Store = new(DirectoryPath, true);
        var admin = new InitialAdministratorPolicy(Hasher).Create("admin", Password);
        Store.Save(new HostState { Initialized = true, SiteName = "Test site", Accounts = [admin] });
        Service = new(Store, Hasher, Driver, Clock, hiperwall: _hiperwall, credentials: _credentials, media: _media, mediaSecrets: _mediaSecrets);
        Admin = Login(); Generation = Service.Acquire(Admin.Token).Generation;
    }
    public LoginResult Login(string name = "admin", string? pc = null) =>
        Service.Login(new(name, Password, Guid.NewGuid(), pc ?? Environment.MachineName));
    public LoginResult Operator(string name, AccountRole role = AccountRole.Operator, bool all = true, Guid[]? ids = null)
    {
        Service.CreateAccount(Admin.Token, new(Generation, name, Password, role, all, ids));
        return Login(name);
    }
    public DeviceConfig Device(string role = "light", string? name = null)
    {
        var device = Service.SaveDevice(Admin.Token, new(Generation, Guid.NewGuid(), Guid.NewGuid(),
            Environment.MachineName, name ?? role, "shared", "test", LatencyMs: 0));
        Service.SaveRole(Admin.Token, new(Generation, role, device.Id));
        return device;
    }
    public ScenarioDefinition Scenario(params ScenarioStep[] steps) =>
        Service.SaveScenario(Admin.Token, new(Generation, Guid.NewGuid(), "Test sequence", steps));
    public SubmitRequest Manual(string role = "light", int delay = 0) =>
        new(Guid.NewGuid(), Generation, role, DelayBeforeMs: delay);
    public Job SubmitScenario(ScenarioDefinition scenario) =>
        Service.Submit(Admin.Token, new(Guid.NewGuid(), Generation, null, ScenarioId: scenario.Id));
    public Job Job(Guid id) => Service.GetState(Admin.Token).Jobs.Single(j => j.Id == id);
    public void Restart()
    {
        Store.Dispose(); Store = new(DirectoryPath);
        Service = new(Store, Hasher, Driver, Clock, hiperwall: _hiperwall, credentials: _credentials, media: _media, mediaSecrets: _mediaSecrets);
        Admin = Login();
    }
    public void Dispose()
    {
        Store.Dispose();
        var full = Path.GetFullPath(DirectoryPath);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IntegratedControTests")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup path escaped");
        Directory.Delete(full, true);
    }
    public static void Reject(string code, Action action) => Assert.Equal(code, Assert.Throws<DomainException>(action).Code);
}
