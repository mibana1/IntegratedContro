using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

internal sealed partial class CameraService
{
    private readonly ILocalMediaSettings? _localMedia;
    private static bool Restoring(LocalMediaChange change) => change.Phase is LocalMediaChangePhase.Restoring or LocalMediaChangePhase.AwaitingRestoreVerification;
    private static MediaConfiguration Target(LocalMediaChange change) => Restoring(change)
        ? change.Before with { Version = change.After.Version + 1 } : change.After;

    private LocalMediaSettingsView? LocalMediaStatus(MediaConfiguration? configuration)
    {
        var change = _host.Current.LocalMediaChange;
        if (change is not null)
        {
            var message = change.Phase switch
            {
                LocalMediaChangePhase.Prepared => "파일 적용이 완료되지 않았습니다. 다시 적용하거나 이전 설정으로 복구하세요.",
                LocalMediaChangePhase.Restoring => "이전 설정 복구가 완료되지 않았습니다. 파일 적용을 다시 시도하세요.",
                LocalMediaChangePhase.AwaitingRestoreVerification => "이전 설정 파일·접속 정보를 복구했습니다. 영상 서버 적용 확인이 필요합니다.",
                _ => "새 설정 파일·접속 정보를 저장했습니다. 영상 서버 적용 확인이 필요합니다."
            };
            return new(true, false, change.Id, change.Phase,
                (change.Failure is null ? "" : change.Failure + "\n") + message +
                "\n인증이 바뀌지 않았거나 서버가 꺼져 있으면 MediaMTX를 재시작한 뒤 적용 확인을 누르세요. 확인 전 영상 처리를 일시 중지합니다.");
        }
        try
        {
            if (_localMedia?.IsManaged != true) return null;
            Require(configuration is not null, "local_media_missing", "로컬 영상 서버 최초 설정을 완료하세요.");
            _localMedia.Validate(configuration!, Credentials(configuration!));
            return new(true, true, null, null, "ControlHost PC의 로컬 영상 서버입니다. 비밀번호를 변경하면 설정 파일과 접속 정보를 함께 갱신합니다.");
        }
        catch (DomainException e) { return new(true, false, null, null, e.Message); }
    }
    private SessionIdentity LocalMediaOwner(string token, long generation, int version)
    {
        _host.Healthy(); _host.CheckConnections();
        var session = _host.Owner(_host.Current, token, generation); _host.Admin(_host.Current, token);
        MediaAvailable();
        Require(_localMedia is not null, "local_media_unavailable", "호스트의 로컬 영상 서버 설정 기능을 확인하세요.");
        Require(_host.Current.Media?.Version == version, "version_conflict", "영상 설정을 다시 불러오세요.");
        return session;
    }
    private LocalMediaChange PendingLocalMedia(string token, LocalMediaActionRequest request)
    {
        LocalMediaOwner(token, request.Generation, request.ExpectedVersion);
        var change = _host.Current.LocalMediaChange;
        Require(change is not null && change.Id == request.ChangeId, "media_change_changed", "영상 서버 변경 상태를 다시 불러오세요.");
        return change!;
    }
    private void EnterMediaChange() => Require(Interlocked.CompareExchange(ref _mediaWorking, 1, 0) == 0,
        "media_busy", "영상 경로 또는 설정 작업이 진행 중입니다. 잠시 후 다시 시도하세요.");
    public MediaSettingsView ChangeLocalMediaPasswords(string token, ChangeLocalMediaPasswordsRequest request)
    {
        using (_host.Open())
        {
            var session = LocalMediaOwner(token, request.Generation, request.ExpectedVersion);
            Require(_host.Current.LocalMediaChange is null, "media_change_pending", "이전 변경의 적용 확인 또는 복구를 먼저 완료하세요.");
            var before = _host.Current.Media!;
            var previous = Credentials(before);
            var rtsp = _localMedia!.Validate(before, previous);
            Require(!string.IsNullOrEmpty(request.ApiPassword) || !string.IsNullOrEmpty(request.HlsPassword),
                "media_password_required", "변경할 API 또는 HLS 비밀번호를 입력하세요.", 400);
            foreach (var value in new[] { request.ApiPassword, request.HlsPassword }.Where(p => !string.IsNullOrEmpty(p)))
                Require(value is { Length: >= 12 and <= 512 } && !value.Any(char.IsControl),
                    "media_password_invalid", "새 비밀번호는 제어 문자가 없는 12~512자로 입력하세요.", 400);
            var passwords = new MediaCredentials(string.IsNullOrEmpty(request.ApiPassword) ? previous.ApiPassword : request.ApiPassword,
                string.IsNullOrEmpty(request.HlsPassword) ? previous.HlsPassword : request.HlsPassword);
            Require(passwords.ApiPassword != passwords.HlsPassword, "media_password_same", "API와 HLS에 서로 다른 비밀번호를 사용하세요.", 400);
            Require(passwords != previous, "media_password_unchanged", "기존 값과 다른 비밀번호를 입력하세요.", 400);
            EnterMediaChange();
            try
            {
                var reference = _mediaSecrets!.Save(JsonSerializer.Serialize(passwords, JsonDefaults.Options));
                var change = new LocalMediaChange(Guid.NewGuid(), before, before with { Version = before.Version + 1, CredentialId = reference },
                    rtsp, LocalMediaChangePhase.Prepared);
                var next = _host.Draft(); next.LocalMediaChange = change;
                _host.Audit(next, session.Info.UserId, "LocalMediaChangePrepared", $"change={change.Id}");
                try { _host.Commit(next); } catch { TryDeleteUncommittedSecret(reference); throw; }
                // Persist the journal before either file changes, so interruptions remain recoverable.
                ApplyLocalMedia(change, session.Info.UserId);
                return MediaSettings(_host.Current.Media);
            }
            finally { Volatile.Write(ref _mediaWorking, 0); }
        }
    }
    public MediaSettingsView RetryLocalMediaChange(string token, LocalMediaActionRequest request)
    {
        using (_host.Open())
        {
            var change = PendingLocalMedia(token, request);
            var session = _host.Authenticate(token);
            EnterMediaChange();
            try { ApplyLocalMedia(change, session.Info.UserId); return MediaSettings(_host.Current.Media); }
            finally { Volatile.Write(ref _mediaWorking, 0); }
        }
    }
    public MediaSettingsView RestoreLocalMediaSettings(string token, LocalMediaActionRequest request)
    {
        using (_host.Open())
        {
            var change = PendingLocalMedia(token, request);
            var session = _host.Authenticate(token);
            EnterMediaChange();
            try
            {
                var next = _host.Draft();
                change = change with { Phase = LocalMediaChangePhase.Restoring, Failure = null };
                next.LocalMediaChange = change;
                _host.Audit(next, session.Info.UserId, "LocalMediaRestoreRequested", $"change={change.Id}");
                _host.Commit(next);
                ApplyLocalMedia(change, session.Info.UserId);
                return MediaSettings(_host.Current.Media);
            }
            finally { Volatile.Write(ref _mediaWorking, 0); }
        }
    }
    private void ApplyLocalMedia(LocalMediaChange change, Guid userId)
    {
        try { _localMedia!.Apply(change, Credentials(change.Before), Credentials(change.After)); }
        catch (DomainException error)
        {
            var failed = _host.Draft(); failed.LocalMediaChange = change with { Failure = error.Message };
            _host.Commit(failed); return;
        }
        var next = _host.Draft();
        next.Media = Target(change);
        next.LocalMediaChange = change with { Phase = Restoring(change) ? LocalMediaChangePhase.AwaitingRestoreVerification : LocalMediaChangePhase.AwaitingVerification, Failure = null };
        next.CameraCleanup = next.CameraCleanup.Select(c => SameMediaTarget(c.Configuration, change.Before)
            ? c with { Configuration = next.Media } : c).ToList();
        next.Cameras = next.Cameras.Select(c => c with { NextSyncAt = _host.Now }).ToList();
        _host.Audit(next, userId, "LocalMediaFilesApplied", $"change={change.Id}; version={next.Media.Version}");
        _host.Commit(next);
    }
    private static bool SameMediaTarget(MediaConfiguration first, MediaConfiguration second) =>
        first.ApiEndpoint == second.ApiEndpoint && first.HlsEndpoint == second.HlsEndpoint &&
        first.ApiUser == second.ApiUser && first.HlsUser == second.HlsUser;

    public async Task<MediaSettingsView> VerifyLocalMediaSettingsAsync(string token, LocalMediaActionRequest request, CancellationToken ct)
    {
        LocalMediaChange change;
        using (_host.Open())
        {
            change = PendingLocalMedia(token, request);
            Require(change.Phase is LocalMediaChangePhase.AwaitingVerification or LocalMediaChangePhase.AwaitingRestoreVerification,
                "media_files_pending", "파일 적용 또는 복구를 먼저 완료하세요.");
            EnterMediaChange();
        }
        try
        {
            string? failure = null;
            try
            {
                var target = Target(change);
                var credentials = Credentials(target);
                _localMedia!.Validate(target, credentials);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _mediaStopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(32));
                await _localMedia.VerifyAsync(target, credentials,
                    Credentials(Restoring(change) ? change.After : change.Before), change.Id, deadline.Token);
            }
            catch (Exception error) when (error is DomainException or HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                failure = "영상 서버의 API/HLS 인증 적용을 확인하지 못했습니다. 재시작 후 다시 확인하거나 이전 설정을 복구하세요.";
            }
            using (_host.Open())
            {
                // Recheck authorization and journal after network I/O; no host lock spans an await.
                PendingLocalMedia(token, request);
                var session = _host.Authenticate(token);
                if (failure is null)
                {
                    try { _localMedia!.Validate(Target(change), Credentials(Target(change))); }
                    catch (DomainException error) { failure = error.Message; }
                }
                var next = _host.Draft();
                if (failure is not null) next.LocalMediaChange = change with { Failure = failure };
                else
                {
                    next.LocalMediaChange = null;
                    next.MediaSecretsToDelete.Add(Restoring(change) ? change.After.CredentialId : change.Before.CredentialId);
                    next.Cameras = next.Cameras.Select(c => c with { NextSyncAt = _host.Now }).ToList();
                    _host.Audit(next, session.Info.UserId, "LocalMediaChangeVerified", $"change={change.Id}; restored={Restoring(change)}");
                }
                _host.Commit(next);
                return MediaSettings(_host.Current.Media);
            }
        }
        finally { Volatile.Write(ref _mediaWorking, 0); }
    }
}
