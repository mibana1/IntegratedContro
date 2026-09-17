using IntegratedContro.Core;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public CameraCatalog GetCameras(string token) => _cameras.GetCameras(token);
    public MediaSettingsView SaveMediaSettings(string token, SaveMediaSettingsRequest request) => _cameras.SaveMediaSettings(token, request);
    public CameraView SaveCamera(string token, SaveCameraRequest request) => _cameras.SaveCamera(token, request);
    public bool SyncCamera(string token, CameraActionRequest request) => _cameras.SyncCamera(token, request);
    public bool DeleteCamera(string token, CameraActionRequest request) => _cameras.DeleteCamera(token, request);
    public bool RetryCameraCleanup(string token, long generation) => _cameras.RetryCameraCleanup(token, generation);
    public Task ReconcileCamerasAsync(CancellationToken ct = default) => _cameras.ReconcileCamerasAsync(ct);
    public Task<CameraConnection> GetCameraStatusAsync(string token, Guid id, int version, CancellationToken ct) => _cameras.GetCameraStatusAsync(token, id, version, ct);
    public Task<MediaPayload> ReadCameraHlsAsync(string token, Guid id, int version, string asset, CancellationToken ct) => _cameras.ReadCameraHlsAsync(token, id, version, asset, ct);
}
