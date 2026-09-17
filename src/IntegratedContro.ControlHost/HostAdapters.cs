using System.Runtime.Versioning;
using IntegratedContro.Application;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.ControlHost;

/// <summary>Environment adapter selection shared by setup and run.</summary>
[SupportedOSPlatform("windows")]
internal static class HostAdapters
{
    public static IPlatformEnvironment Platform { get; } = new WindowsEnvironment();
    private static ISecretProtector Secrets { get; } = new WindowsCurrentUserSecretProtector();
    public static HostCertificate Certificates { get; } = new(Secrets);
    public static HostStorage OpenStorage(string path, bool initialize = false) => SqliteHostStorage.Open(path, initialize);
    public static ICredentialStore HiperwallCredentials(string path) => new HiperwallCredentialStore(path, Secrets);
    public static IMediaSecretStore MediaCredentials(string path) => new MediaCredentialStore(path, Secrets);
}
