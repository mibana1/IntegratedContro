namespace IntegratedContro.Application;

// Values only: registry, native API and process inspection stay in the platform adapter.
public sealed record PlatformEnvironment(bool IsWindows, string InstallationType, string DisplayVersion,
    string Edition, string OsArchitecture, string ProcessArchitecture, string Build, bool DetectionSucceeded = true);

public interface IPlatformEnvironment
{
    PlatformEnvironment Read();
}

// The ciphertext format and protection scope belong to the selected adapter, not the credential repositories.
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed record PlatformSupport(bool OfficiallySupported, string Description);
