using IntegratedContro.Core;

namespace IntegratedContro.Application;

public interface IStateStore : IDisposable
{
    HostState Load();
    void Save(HostState state);
}
public interface ICredentialStore
{
    Guid Save(string secret);
    string Read(Guid reference);
}
public interface IHiperwallReader
{
    Task<HiperwallReading> ReadAsync(HiperwallConfiguration configuration, string? secret, CancellationToken cancellationToken);
}
public interface IHiperwallWriter
{
    Task<HiperwallWriteResult> WriteAsync(HiperwallConfiguration configuration, string? secret,
        HiperwallWireCommand command, CancellationToken cancellationToken);
}
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
public interface IInitialAdministratorPolicy
{
    Account Create(string name, string password);
}
public interface IDeviceDriver
{
    string Id { get; }
    string Version { get; }
    DeviceModel[] Models { get; }
    void ValidateConfiguration(DeviceConfig device);
    Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken cancellationToken);
    Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken cancellationToken);
}
public sealed record DriverResult(StepStatus Status, string Detail, IReadOnlyDictionary<DeviceOperation, int>? Values = null,
    DeviceEvidence Evidence = DeviceEvidence.Simulation);
public sealed record DriverReading(bool Available, IReadOnlyDictionary<DeviceOperation, int> Values, string Detail,
    DeviceEvidence Evidence = DeviceEvidence.Simulation);
public sealed class InitialAdministratorPolicy(IPasswordHasher hasher) : IInitialAdministratorPolicy
{
    public Account Create(string name, string password)
    {
        Validation.AccountName(name);
        Validation.Password(password);
        return new Account { Name = name.Trim(), PasswordHash = hasher.Hash(password), Role = AccountRole.Administrator };
    }
}
public static class Validation
{
    public static void Require(bool condition, string code, string message, int status = 409)
    { if (!condition) throw new DomainException(code, message, status); }
    public static void Text(string? text, string name, int max = 120) =>
        Require(!string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl),
            "invalid_input", $"{name}: 1~{max}자의 유효한 값을 입력하세요.", 400);
    public static void AccountName(string name) => Text(name, "계정 이름", 64);
    public static void Password(string password) =>
        Require(password is { Length: >= 12 and <= 256 }, "password_policy", "비밀번호는 12~256자여야 합니다.", 400);
}
