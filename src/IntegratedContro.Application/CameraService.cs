using System.Text;
using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private readonly IMediaMtxClient? _media;
    private readonly IMediaSecretStore? _mediaSecrets;
    private int _mediaWorking;
    private readonly SemaphoreSlim _mediaReads = new(4, 4);
    private readonly CancellationTokenSource _mediaStopping = new();

    private static MediaSettingsView MediaSettings(MediaConfiguration? c) => c is null
        ? new(0, "", "", "", "", false)
        : new(c.Version, c.ApiEndpoint, c.HlsEndpoint, c.ApiUser, c.HlsUser, true);
    private static CameraView CameraInfo(CameraRegistration c) => new(c.Id, c.Version, c.Name, c.Location,
        c.StreamPath, c.Enabled, c.Provisioning, c.Message, c.UpdatedAt, c.ContentSelector, c.ContentValue);
    public CameraCatalog GetCameras(string token)
    {
        lock (_gate)
        {
            Healthy(); Authenticate(token);
            return new(_state.Cameras.Select(CameraInfo).OrderBy(c => c.Name).ToArray(),
                _state.CameraCleanup.Select(c => new CameraCleanupView(c.Id, c.StreamPath, c.RequesterName, c.Attempts, c.Message)).ToArray(),
                MediaSettings(_state.Media));
        }
    }
    private static string MediaEndpoint(string value)
    {
        Require(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.AbsolutePath == "/" && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "",
            "invalid_endpoint", "http(s)://주소:포트 형식으로 입력하세요. 계정·경로·쿼리는 포함하지 않습니다.", 400);
        return uri!.GetLeftPart(UriPartial.Authority);
    }
    private static void MediaPassword(string? value) => Require(value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl),
        "media_password_required", "MediaMTX API와 HLS 읽기에 각각 전용 비밀번호를 설정하세요.", 400);
    private void MediaAvailable() => Require(_media is not null && _mediaSecrets is not null,
        "media_unavailable", "호스트 영상 어댑터를 확인하세요.", 503);
    public MediaSettingsView SaveMediaSettings(string token, SaveMediaSettingsRequest request)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe(); var session = Owner(_state, token, request.Generation); Admin(_state, token); MediaAvailable();
            var api = MediaEndpoint(request.ApiEndpoint); var hls = MediaEndpoint(request.HlsEndpoint);
            Text(request.ApiUser, "API 사용자", 128); Text(request.HlsUser, "HLS 사용자", 128);
            Require(!request.ApiUser.Contains(':') && !request.HlsUser.Contains(':') && request.ApiUser != request.HlsUser,
                "media_accounts", "API 제어와 HLS 읽기에 서로 다른 전용 사용자 이름을 지정하세요.", 400);
            var old = _state.Media;
            Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "영상 설정을 다시 불러오세요.");
            var sameTarget = old is not null && old.ApiEndpoint == api && old.HlsEndpoint == hls &&
                old.ApiUser == request.ApiUser && old.HlsUser == request.HlsUser;
            Require(sameTarget || _state.Cameras.Count + _state.CameraCleanup.Count == 0,
                "media_in_use", "등록 카메라와 경로 정리 큐가 비워진 뒤 MediaMTX 대상을 변경하세요.");
            MediaCredentials? previous = sameTarget ? Credentials(old!) : null;
            var apiPassword = string.IsNullOrEmpty(request.ApiPassword) ? previous?.ApiPassword : request.ApiPassword;
            var hlsPassword = string.IsNullOrEmpty(request.HlsPassword) ? previous?.HlsPassword : request.HlsPassword;
            MediaPassword(apiPassword); MediaPassword(hlsPassword);
            var secret = _mediaSecrets!.Save(JsonSerializer.Serialize(new MediaCredentials(apiPassword!, hlsPassword!), JsonDefaults.Options));
            var next = JsonDefaults.Copy(_state);
            next.Media = new((old?.Version ?? 0) + 1, api, hls, request.ApiUser, request.HlsUser, secret);
            if (old is not null) next.MediaSecretsToDelete.Add(old.CredentialId);
            next.Cameras = next.Cameras.Select(c => c with { NextSyncAt = Now }).ToList();
            Audit(next, session.Info.UserId, "MediaSettingsSaved", $"version={next.Media.Version}");
            try { Persist(next); } catch { TryDeleteUncommittedSecret(secret); throw; }
            return MediaSettings(next.Media);
        }
    }
    private MediaCredentials Credentials(MediaConfiguration c) =>
        JsonSerializer.Deserialize<MediaCredentials>(_mediaSecrets!.Read(c.CredentialId), JsonDefaults.Options)
        ?? throw new DomainException("media_secret_unavailable", "영상 전용 계정을 다시 입력하세요.", 503);
    private void TryDeleteUncommittedSecret(Guid reference)
    {
        try { _mediaSecrets!.Delete(reference); } catch (DomainException) { /* Original commit failure stays authoritative. */ }
    }
    private static string PrepareSource(SaveCameraRequest r)
    {
        Require(r.RtspUrl is { Length: > 0 and <= 2048 } && !r.RtspUrl.Any(char.IsControl) &&
            Uri.TryCreate(r.RtspUrl, UriKind.Absolute, out var u) && u.Scheme is "rtsp" or "rtsps" &&
            !string.IsNullOrEmpty(u.Host) && u.Fragment == "", "invalid_rtsp", "유효한 rtsp:// 또는 rtsps:// 주소를 입력하세요.", 400);
        Require(string.IsNullOrEmpty(r.UserName) == string.IsNullOrEmpty(r.Password),
            "invalid_rtsp_credentials", "RTSP 사용자와 비밀번호를 함께 입력하세요.", 400);
        Require((r.UserName?.Length ?? 0) <= 256 && (r.Password?.Length ?? 0) <= 512 &&
            !(r.UserName ?? "").Any(char.IsControl) && !(r.Password ?? "").Any(char.IsControl),
            "invalid_rtsp_credentials", "RTSP 계정 길이·제어 문자를 확인하세요.", 400);
        var source = new UriBuilder(r.RtspUrl!);
        if (!string.IsNullOrEmpty(r.UserName))
        { source.UserName = Uri.EscapeDataString(r.UserName); source.Password = Uri.EscapeDataString(r.Password!); }
        return source.Uri.AbsoluteUri;
    }
    private void ValidateCameraMapping(SaveCameraRequest r)
    {
        if (string.IsNullOrEmpty(r.ContentSelector) && string.IsNullOrEmpty(r.ContentValue)) return;
        Require(_state.Hiperwall?.Version == r.HiperwallConfigurationVersion &&
            _hiperwallView?.ConfigurationVersion == r.HiperwallConfigurationVersion &&
            _hiperwallView.Contents.State == HiperwallListState.Available,
            "mapping_inventory_required", "현재 Hiperwall Contents를 조회하고 매핑 대상을 선택하세요.");
        Require(PreviewMatches(_hiperwallView!.Contents, r.ContentSelector ?? "", r.ContentValue ?? ""),
            "mapping_not_unique", "현재 목록에서 유일한 UUID 또는 전체 이름만 매핑할 수 있습니다.", 400);
    }
    public CameraView SaveCamera(string token, SaveCameraRequest request)
    {
        lock (_gate)
        {
            Healthy(); CheckConnectionUnsafe(); var session = Owner(_state, token, request.Generation); Admin(_state, token); MediaAvailable();
            Require(_state.Media is not null, "media_not_configured", "MediaMTX 설정을 먼저 저장하세요.");
            Require(request.Id != Guid.Empty, "camera_id_required", "새 카메라 ID가 필요합니다.", 400);
            Text(request.Name, "카메라 이름", 100);
            Require(request.Location is { Length: <= 200 } && !request.Location.Any(char.IsControl),
                "invalid_location", "위치는 제어 문자 없이 200자 이내로 입력하세요.", 400);
            var old = _state.Cameras.SingleOrDefault(c => c.Id == request.Id);
            Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "카메라 목록을 새로 조회하세요.");
            Require(old is null || !old.DeleteRequested, "camera_deleting", "카메라 삭제 결과를 확인하세요.");
            Require(old is not null || _state.Cameras.Count < 500, "camera_limit", "카메라 등록 한도는 500개입니다.");
            if (old is null || old.ContentSelector != request.ContentSelector || old.ContentValue != request.ContentValue)
                ValidateCameraMapping(request);
            Require(!string.IsNullOrEmpty(request.RtspUrl) || old is not null &&
                string.IsNullOrEmpty(request.UserName) && string.IsNullOrEmpty(request.Password),
                "source_required", "새 카메라 또는 RTSP 계정 변경 시 RTSP 주소도 입력하세요.", 400);
            var source = string.IsNullOrEmpty(request.RtspUrl) ? (Guid?)null : _mediaSecrets!.Save(PrepareSource(request));
            var next = JsonDefaults.Copy(_state);
            var camera = new CameraRegistration(request.Id, (old?.Version ?? 0) + 1, request.Name.Trim(), request.Location.Trim(),
                old?.StreamPath ?? $"ic-{_state.SiteId:N}-{Guid.NewGuid():N}", source ?? old!.SourceCredentialId,
                request.Enabled, CameraProvisioning.Pending, "등록 저장 완료 · MediaMTX 경로 준비 대기", Now, Now,
                ContentSelector: string.IsNullOrEmpty(request.ContentSelector) ? null : request.ContentSelector,
                ContentValue: string.IsNullOrEmpty(request.ContentValue) ? null : request.ContentValue);
            next.Cameras.RemoveAll(c => c.Id == request.Id); next.Cameras.Add(camera);
            if (source is not null && old is not null) next.MediaSecretsToDelete.Add(old.SourceCredentialId);
            Audit(next, session.Info.UserId, "CameraSaved", $"camera={camera.Id}; version={camera.Version}; enabled={camera.Enabled}");
            try { Persist(next); } catch { if (source is { } reference) TryDeleteUncommittedSecret(reference); throw; }
            return CameraInfo(camera);
        }
    }
    public bool SyncCamera(string token, CameraActionRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token); MediaAvailable();
        var camera = FindCamera(s, request);
        Require(!camera.DeleteRequested, "camera_deleting", "삭제 처리 중에는 재동기화할 수 없습니다.");
        s.Cameras[s.Cameras.IndexOf(camera)] = camera with { Provisioning = CameraProvisioning.Pending,
            Message = "재동기화 접수 · 경로 준비 대기", NextSyncAt = Now };
        Audit(s, session.Info.UserId, "CameraSyncRequested", $"camera={camera.Id}");
        return true;
    });
    private static CameraRegistration FindCamera(HostState s, CameraActionRequest r)
    {
        var camera = s.Cameras.SingleOrDefault(c => c.Id == r.Id);
        Require(camera is not null, "camera_not_found", "등록 카메라가 없습니다.", 404);
        Require(camera!.Version == r.ExpectedVersion, "version_conflict", "카메라 목록을 새로 조회하세요.");
        return camera;
    }
    public bool DeleteCamera(string token, CameraActionRequest request) => Change(s =>
    {
        var session = Owner(s, token, request.Generation); Admin(s, token); MediaAvailable();
        var camera = FindCamera(s, request);
        if (request.Force)
        {
            Require(s.CameraCleanup.Count < 1000, "cleanup_limit", "경로 정리 큐를 먼저 처리하세요.");
            s.CameraCleanup.Add(new(Guid.NewGuid(), s.Media!, camera.StreamPath, camera.SourceCredentialId,
                session.Info.UserId, session.Info.UserName, Now));
            s.Cameras.Remove(camera);
            Audit(s, session.Info.UserId, "CameraForceDeleted", $"camera={camera.Id}; path={camera.StreamPath}");
        }
        else
        {
            Require(!camera.DeleteRequested, "camera_deleting", "삭제가 이미 접수되었습니다.");
            s.Cameras[s.Cameras.IndexOf(camera)] = camera with { Version = camera.Version + 1, DeleteRequested = true,
                Provisioning = CameraProvisioning.Deleting, NextSyncAt = Now, Message = "경로 삭제 결과 대기 · 등록 유지" };
            Audit(s, session.Info.UserId, "CameraDeleteRequested", $"camera={camera.Id}");
        }
        return true;
    });
    public bool RetryCameraCleanup(string token, long generation) => Change(s =>
    {
        var session = Owner(s, token, generation); Admin(s, token);
        s.CameraCleanup = s.CameraCleanup.Select(c => c with { NextAttemptAt = Now }).ToList();
        Audit(s, session.Info.UserId, "CameraCleanupRequested", $"count={s.CameraCleanup.Count}");
        return true;
    });
    public async Task ReconcileCamerasAsync(CancellationToken ct = default)
    {
        if (_media is null || _mediaSecrets is null || Interlocked.CompareExchange(ref _mediaWorking, 1, 0) != 0) return;
        try
        {
            CameraRegistration? camera; CameraCleanup? cleanup; MediaConfiguration? config;
            lock (_gate)
            {
                if (_stopping || _storageFailed || ct.IsCancellationRequested) return;
                cleanup = _state.CameraCleanup.FirstOrDefault(c => c.NextAttemptAt <= Now);
                camera = cleanup is null ? _state.Cameras.OrderBy(c => c.NextSyncAt).FirstOrDefault(c => c.NextSyncAt <= Now) : null;
                config = cleanup?.Configuration ?? _state.Media;
            }
            if (config is null) return;
            string? failure = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _mediaStopping.Token);
                timeout.CancelAfter(10000);
                var credentials = Credentials(config);
                if (cleanup is not null)
                    await _media.DeletePathAsync(config, credentials, cleanup.StreamPath, timeout.Token);
                else if (camera is not null)
                {
                    if (camera.DeleteRequested || !camera.Enabled)
                        await _media.DeletePathAsync(config, credentials, camera.StreamPath, timeout.Token);
                    else await _media.EnsurePathAsync(config, credentials, camera.StreamPath,
                        _mediaSecrets.Read(camera.SourceCredentialId), timeout.Token);
                }
            }
            catch (Exception e) when (e is DomainException or HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
            { failure = e is DomainException d ? d.Message : "MediaMTX 연결·응답을 확인하고 재동기화하세요."; }
            lock (_gate)
            {
                if (_storageFailed || _stopping) return;
                if (cleanup is not null || camera is not null)
                {
                    var next = JsonDefaults.Copy(_state);
                    if (cleanup is not null)
                    {
                        var item = next.CameraCleanup.Single(c => c.Id == cleanup.Id);
                        if (failure is null)
                        {
                            next.CameraCleanup.Remove(item); next.MediaSecretsToDelete.Add(item.SourceCredentialId);
                            if (!next.MediaSecretsToDelete.Contains(item.Configuration.CredentialId))
                                next.MediaSecretsToDelete.Add(item.Configuration.CredentialId);
                            Audit(next, item.RequestedBy, "CameraPathCleaned", $"path={item.StreamPath}");
                        }
                        else next.CameraCleanup[next.CameraCleanup.IndexOf(item)] = item with { Attempts = item.Attempts + 1,
                            NextAttemptAt = Now.AddSeconds(30), Message = failure };
                    }
                    else if (camera is not null && next.Cameras.SingleOrDefault(c => c.Id == camera.Id) is { } current &&
                        current.Version == camera.Version && next.Media?.Version == config.Version)
                    {
                        if (failure is null && camera.DeleteRequested)
                        {
                            next.Cameras.Remove(current); next.MediaSecretsToDelete.Add(current.SourceCredentialId);
                            Audit(next, null, "CameraDeleted", $"camera={camera.Id}; path={camera.StreamPath}");
                        }
                        else
                        {
                            var status = failure is not null ? camera.DeleteRequested ? CameraProvisioning.DeleteFailed : CameraProvisioning.Failed :
                                camera.Enabled ? CameraProvisioning.Ready : CameraProvisioning.Disabled;
                            var message = failure ?? (camera.Enabled ? "경로 준비 완료 · 실제 입력 상태는 별도로 조회합니다." : "비활성 · MediaMTX 경로 정리 완료");
                            next.Cameras[next.Cameras.IndexOf(current)] = current with { Provisioning = status, Message = message,
                                UpdatedAt = Now, NextSyncAt = status == CameraProvisioning.DeleteFailed ? DateTimeOffset.MaxValue : Now.AddSeconds(30),
                                DeleteRequested = false };
                            if (current.Provisioning != status || current.Message != message)
                                Audit(next, null, "CameraReconciled", $"camera={camera.Id}; state={status}");
                        }
                    }
                    Persist(next); // A late sync result can never recreate a force-deleted registration.
                }
                CollectMediaSecrets();
            }
        }
        finally { Volatile.Write(ref _mediaWorking, 0); }
    }
    private void CollectMediaSecrets()
    {
        var live = _state.Cameras.Select(c => c.SourceCredentialId)
            .Concat(_state.CameraCleanup.SelectMany(c => new[] { c.SourceCredentialId, c.Configuration.CredentialId }))
            .Concat(_state.Media is { } m ? new[] { m.CredentialId } : []).ToHashSet();
        var deleted = new List<Guid>();
        foreach (var id in _state.MediaSecretsToDelete.Distinct().Where(id => !live.Contains(id)).Take(20))
        {
            try { _mediaSecrets!.Delete(id); deleted.Add(id); } catch (DomainException) { break; }
        }
        if (deleted.Count == 0) return;
        var next = JsonDefaults.Copy(_state);
        next.MediaSecretsToDelete.RemoveAll(deleted.Contains); Persist(next);
    }
    private (CameraRegistration Camera, MediaConfiguration Configuration) ReadableCamera(string token, Guid id, int version)
    {
        Healthy(); Authenticate(token); MediaAvailable();
        var camera = _state.Cameras.SingleOrDefault(c => c.Id == id);
        Require(camera is not null && camera.Version == version, "camera_changed", "카메라가 변경·삭제되었습니다. 다시 선택하세요.", 409);
        Require(camera!.Enabled && !camera.DeleteRequested && camera.Provisioning == CameraProvisioning.Ready &&
            _state.Media is not null, "camera_not_ready", "활성 카메라의 경로 준비 결과를 확인하세요.");
        return (camera, _state.Media!);
    }
    public async Task<CameraConnection> GetCameraStatusAsync(string token, Guid id, int version, CancellationToken ct)
    {
        CameraRegistration camera; MediaConfiguration config;
        lock (_gate) (camera, config) = ReadableCamera(token, id, version);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _mediaStopping.Token); timeout.CancelAfter(8000);
        Require(await _mediaReads.WaitAsync(0, timeout.Token), "media_busy", "영상 조회가 많습니다. 잠시 후 다시 시도하세요.", 429);
        try
        {
            CameraConnection status;
            try { status = await _media!.GetStatusAsync(config, Credentials(config), camera.StreamPath, timeout.Token); }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException)
            { throw new DomainException("media_status_failed", "실제 영상 입력을 조회하지 못했습니다.", 502); }
            lock (_gate)
            {
                ReadableCamera(token, id, version);
                Require(_state.Media?.Version == config.Version, "media_changed", "영상 설정이 변경되었습니다.");
            }
            return status;
        }
        finally { _mediaReads.Release(); }
    }
    public async Task<MediaPayload> ReadCameraHlsAsync(string token, Guid id, int version, string asset, CancellationToken ct)
    {
        Require(MediaLimits.ValidAsset(asset), "invalid_asset", "허용된 HLS 파일 이름만 요청할 수 있습니다.", 400);
        CameraRegistration camera; MediaConfiguration config;
        lock (_gate) (camera, config) = ReadableCamera(token, id, version);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _mediaStopping.Token); timeout.CancelAfter(15000);
        Require(await _mediaReads.WaitAsync(0, timeout.Token), "media_busy", "동시 영상 조회 한도를 초과했습니다.", 429);
        try
        {
            MediaPayload payload;
            try { payload = await _media!.ReadHlsAsync(config, Credentials(config), camera.StreamPath, asset, timeout.Token); }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException or DecoderFallbackException)
            { throw new DomainException("hls_failed", "HLS 영상을 받지 못했습니다. 연결과 코덱·영상 상태를 확인하세요.", 502); }
            lock (_gate)
            {
                ReadableCamera(token, id, version);
                Require(_state.Media?.Version == config.Version, "media_changed", "영상 설정이 변경되었습니다.");
                ct.ThrowIfCancellationRequested();
            }
            return payload;
        }
        finally { _mediaReads.Release(); }
    }
    private void RecoverCameras()
    {
        if (_state.Cameras.Count == 0 && _state.CameraCleanup.Count == 0) return;
        var next = JsonDefaults.Copy(_state);
        next.Cameras = next.Cameras.Select(c => c.Provisioning == CameraProvisioning.DeleteFailed ? c : c with { NextSyncAt = Now,
            Provisioning = c.DeleteRequested ? CameraProvisioning.Deleting : CameraProvisioning.Pending,
            Message = "호스트 시작 · 저장 등록과 경로 재동기화 대기" }).ToList();
        next.CameraCleanup = next.CameraCleanup.Select(c => c with { NextAttemptAt = Now }).ToList();
        Persist(next);
    }
}
