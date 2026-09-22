using IntegratedContro.Core;

namespace IntegratedContro.Application;

public interface IMediaSecretStore : ICredentialStore { void Delete(Guid reference); }
public interface ILocalMediaSettings
{
    bool IsManaged { get; }
    int Validate(MediaConfiguration configuration, MediaCredentials credentials);
    void Apply(LocalMediaChange change, MediaCredentials before, MediaCredentials after);
    Task VerifyAsync(MediaConfiguration configuration, MediaCredentials credentials,
        MediaCredentials rejectedCredentials, Guid changeId, CancellationToken cancellationToken);
}
public interface IMediaMtxClient
{
    Task EnsurePathAsync(MediaConfiguration configuration, MediaCredentials credentials, string path,
        string source, CancellationToken cancellationToken);
    Task DeletePathAsync(MediaConfiguration configuration, MediaCredentials credentials, string path,
        CancellationToken cancellationToken);
    Task<CameraConnection> GetStatusAsync(MediaConfiguration configuration, MediaCredentials credentials,
        string path, CancellationToken cancellationToken);
    Task<MediaPayload> ReadHlsAsync(MediaConfiguration configuration, MediaCredentials credentials,
        string path, string asset, CancellationToken cancellationToken);
}
public interface IHiperwallPreviewReader
{
    Task<MediaPayload> ReadPreviewAsync(HiperwallConfiguration configuration, string? secret,
        string selector, string value, CancellationToken cancellationToken);
}
