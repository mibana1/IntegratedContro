using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class CameraViewModel
{
    public bool IsLocalMedia => _settings.LocalServer?.Managed == true;
    public bool HasLocalMediaChange => _settings.LocalServer?.ChangeId is not null;
    public bool CanEditMediaTarget => CanConfigure && !IsLocalMedia;
    public bool CanEditMediaPasswords => CanConfigure && (!IsLocalMedia || _settings.LocalServer?.CanChange == true);
    public string LocalMediaMessage => _settings.LocalServer?.Message ?? "";
    public string SaveMediaLabel => IsLocalMedia ? "로컬 서버 비밀번호 변경" : "영상 연결 설정 저장";
    public Func<string, bool> ConfirmLocalMediaChange { get; set; } = _ => false;
    public AsyncCommand RetryLocalMediaCommand { get; private set; } = null!;
    public AsyncCommand RestoreLocalMediaCommand { get; private set; } = null!;
    public AsyncCommand VerifyLocalMediaCommand { get; private set; } = null!;

    private void InitializeLocalMediaCommands()
    {
        RetryLocalMediaCommand = Command(ct => LocalMediaAction("retry", ct), () => CanConfigure && HasLocalMediaChange);
        RestoreLocalMediaCommand = Command(async ct =>
        {
            if (ConfirmLocalMediaChange("변경 전 영상 서버 비밀번호로 설정 파일과 접속 정보를 복구합니다.\n복구 후 적용 확인이 필요하며, 서버가 설정을 읽지 못하면 MediaMTX를 재시작해야 합니다. 계속할까요?"))
                await LocalMediaAction("restore", ct);
        }, () => CanConfigure && HasLocalMediaChange);
        VerifyLocalMediaCommand = Command(ct => LocalMediaAction("verify", ct), () => CanConfigure &&
            _settings.LocalServer?.Phase is LocalMediaChangePhase.AwaitingVerification or LocalMediaChangePhase.AwaitingRestoreVerification);
    }
    private async Task LocalMediaAction(string action, CancellationToken ct)
    {
        var settings = await _client!.Post<MediaSettingsView>("/api/media/local/" + action,
            new LocalMediaActionRequest(Generation, _settings.Version, _settings.LocalServer!.ChangeId!.Value), ct,
            timeoutMs: action == "verify" ? 40000 : 8000);
        ct.ThrowIfCancellationRequested(); _settings = settings;
        PublishStatus(HasLocalMediaChange ? LocalMediaMessage : "로컬 영상 서버 API/HLS 인증 적용 확인 완료 · 카메라를 다시 동기화합니다.");
        ClearSecrets(); Changed(nameof(AppliedMedia)); Raise();
    }
    private void NotifyLocalMedia()
    {
        foreach (var name in new[] { nameof(IsLocalMedia), nameof(HasLocalMediaChange), nameof(CanEditMediaTarget),
            nameof(CanEditMediaPasswords), nameof(LocalMediaMessage), nameof(SaveMediaLabel) }) Changed(name);
    }
}
