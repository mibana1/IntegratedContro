namespace IntegratedContro.Core;

public enum CameraProvisioning { Pending, Ready, Disabled, Failed, Deleting, DeleteFailed }
public sealed record MediaConfiguration(int Version, string ApiEndpoint, string HlsEndpoint, string ApiUser,
    string HlsUser, Guid CredentialId);
public sealed record MediaSettingsView(int Version, string ApiEndpoint, string HlsEndpoint, string ApiUser,
    string HlsUser, bool HasSecret)
{
    public LocalMediaSettingsView? LocalServer { get; init; }
}
public enum LocalMediaChangePhase { Prepared, AwaitingVerification, Restoring, AwaitingRestoreVerification }
public sealed record LocalMediaChange(Guid Id, MediaConfiguration Before, MediaConfiguration After,
    int RtspPort, LocalMediaChangePhase Phase, string? Failure = null);
public sealed record LocalMediaSettingsView(bool Managed, bool CanChange, Guid? ChangeId,
    LocalMediaChangePhase? Phase, string Message);
public sealed record ChangeLocalMediaPasswordsRequest(long Generation, int ExpectedVersion, string? ApiPassword, string? HlsPassword);
public sealed record LocalMediaActionRequest(long Generation, int ExpectedVersion, Guid ChangeId);
public sealed record SaveMediaSettingsRequest(long Generation, int ExpectedVersion, string ApiEndpoint,
    string HlsEndpoint, string ApiUser, string HlsUser, string? ApiPassword = null, string? HlsPassword = null);
public sealed record CameraRegistration(Guid Id, int Version, string Name, string Location, string StreamPath,
    Guid SourceCredentialId, bool Enabled, CameraProvisioning Provisioning, string Message,
    DateTimeOffset UpdatedAt, DateTimeOffset NextSyncAt, bool DeleteRequested = false,
    string? ContentSelector = null, string? ContentValue = null);
public sealed record CameraView(Guid Id, int Version, string Name, string Location, string StreamPath,
    bool Enabled, CameraProvisioning Provisioning, string Message, DateTimeOffset UpdatedAt,
    string? ContentSelector, string? ContentValue)
{
    public string StateLabel => Provisioning switch {
        CameraProvisioning.Pending => "경로 준비 대기", CameraProvisioning.Ready => "경로 준비 완료",
        CameraProvisioning.Disabled => "비활성 · 경로 정리됨", CameraProvisioning.Failed => "경로 준비 실패",
        CameraProvisioning.Deleting => "삭제 처리 중", _ => "삭제 실패 · 등록 유지" };
}
public sealed record CameraCatalog(CameraView[] Cameras, CameraCleanupView[] Cleanup, MediaSettingsView Settings);
public sealed record SaveCameraRequest(long Generation, Guid Id, int ExpectedVersion, string Name, string Location,
    bool Enabled, string? RtspUrl = null, string? UserName = null, string? Password = null,
    string? ContentSelector = null, string? ContentValue = null, int HiperwallConfigurationVersion = 0);
public sealed record CameraActionRequest(long Generation, Guid Id, int ExpectedVersion, bool Force = false);
public sealed record CameraCleanup(Guid Id, MediaConfiguration Configuration, string StreamPath,
    Guid SourceCredentialId, Guid RequestedBy, string RequesterName, DateTimeOffset NextAttemptAt,
    int Attempts = 0, string Message = "경로 정리 대기");
public sealed record CameraCleanupView(Guid Id, string StreamPath, string RequesterName, int Attempts, string Message);
public sealed record CameraConnection(bool Ready, string Message);
public sealed record MediaCredentials(string ApiPassword, string HlsPassword);
public sealed record MediaPayload(byte[] Bytes, string ContentType);
public sealed record HiperwallPreviewRequest(int ConfigurationVersion, string Selector, string Value);
public enum VideoPlaybackState { Stopped, Connecting, Playing, Reconnecting, Failed }
public sealed record VideoPlaybackStatus(VideoPlaybackState State, string Message, int Attempt = 0);
public sealed record VideoSource(Uri Uri, string UserName, string Password);
// No native handles or UI types cross this contract.
public interface IVideoPlayer : IAsyncDisposable
{
    event Action<VideoPlaybackStatus>? StatusChanged;
    Task PlayAsync(VideoSource source, CancellationToken cancellationToken);
    bool Muted { get; set; }
}
