using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class CameraViewModel
{
    private bool _mediaLoaded, _mediaReadFailed, _mediaEditorOpen;
    private int _mediaDraftVersion;
    public bool HasMediaSettings => _mediaLoaded && _settings.Version > 0 && _settings.HasSecret;
    public bool IsLocalMedia => _settings.LocalServer?.Managed == true;
    public bool HasLocalMediaChange => _settings.LocalServer?.ChangeId is not null;
    public bool CanManageMedia => CanConfigure && _supported && _mediaLoaded && !_mediaReadFailed;
    public bool MediaDraftConflict => _mediaEditorOpen && _mediaDraftVersion != _settings.Version;
    public bool CanEditMediaTarget => CanManageMedia && _mediaEditorOpen && !IsLocalMedia;
    public bool CanEditMediaPasswords => CanManageMedia && _mediaEditorOpen && !MediaDraftConflict &&
        (!IsLocalMedia || _settings.LocalServer?.CanChange == true);
    public bool IsMediaEditorOpen
    {
        get => _mediaEditorOpen;
        set
        {
            if (value == _mediaEditorOpen || (value && !CanManageMedia)) return;
            _mediaEditorOpen = value;
            CopyMediaSettingsToEditor(); ClearMediaSecrets(); Raise();
        }
    }
    public string MediaStatus => !_connected ? "호스트 연결 대기" : !_supported ? "호스트 영상 기능 확인 필요" :
        _mediaReadFailed ? "호스트 영상 설정 조회 실패" : !_mediaLoaded ? "호스트 영상 설정 불러오는 중" :
        !HasMediaSettings ? "호스트 영상 서버 설정 필요" : HasLocalMediaChange ? "호스트 영상 서버 적용 확인 필요" :
        "호스트 영상 설정 자동 사용";
    public string MediaDescription => !_connected ? "호스트에 연결하면 저장된 영상 설정을 자동으로 불러옵니다." :
        !_supported ? "영상 기능을 지원하는 ControlHost에 접속하세요." :
        _mediaReadFailed ? "서버 실행 상태와 네트워크를 확인하세요. 연결이 복구되면 다시 자동 조회합니다." :
        !_mediaLoaded ? "접속한 호스트의 설정을 확인하고 있습니다." :
        !HasMediaSettings ? "호스트에 등록된 영상 설정이 없습니다. 호스트 PC에서 앱을 정상 종료한 뒤 다시 실행하고 서버 시작 질문에 ‘아니요’를 선택하세요. 로그인 화면의 ‘초기 설정 · 저장 위치’ → ‘기존 데이터 사용’에서 ‘영상 서버 사용’을 켜고 ‘자동 생성 · 연결 확인 (이 PC)’으로 저장하면 주소·계정·비밀번호가 자동 설정됩니다. 별도 영상 서버는 아래 관리자 고급 설정에서 등록하세요." :
        HasLocalMediaChange ? "관리자가 아래 안내에 따라 적용 확인 또는 복구를 마쳐야 합니다." :
        "이 PC에서는 MediaMTX 정보를 입력할 필요가 없습니다. 영상은 접속한 호스트를 통해 받으며 실제 재생 여부는 카메라를 선택해 확인하세요.";
    public string AppliedMedia => !HasMediaSettings ? "" :
        $"호스트 기준 주소 · 적용 v{_settings.Version}\nAPI {_settings.ApiEndpoint}\nHLS {_settings.HlsEndpoint}\nAPI 계정 {_settings.ApiUser}\nHLS 계정 {_settings.HlsUser}\n비밀번호 저장됨 · 원문은 표시하지 않습니다.";
    public string MediaEditorMessage => MediaDraftConflict ?
        "다른 작업에서 호스트 설정이 변경되었습니다. 입력을 확인한 뒤 고급 설정을 닫았다 다시 열어 최신 설정으로 편집하세요." :
        "변경은 접속한 호스트에 저장되며 같은 호스트를 사용하는 모든 PC에 적용됩니다. 고급 설정을 닫으면 저장하지 않은 입력을 취소합니다.";
    public string MediaEditorLabel => IsLocalMedia ? "관리자 고급 설정 · 비밀번호 변경" : "관리자 고급 설정 · 외부 서버 연결";
    public string LocalMediaMessage => _settings.LocalServer?.Message ?? "";
    public string SaveMediaLabel => IsLocalMedia ? "호스트 영상 서버 비밀번호 변경" : "호스트 영상 연결 설정 저장";
    public Action ClearMediaSecrets { get; set; } = () => { };
    public Func<string, bool> ConfirmLocalMediaChange { get; set; } = _ => false;
    public AsyncCommand SaveSettingsCommand { get; private set; } = null!;
    public AsyncCommand RetryLocalMediaCommand { get; private set; } = null!;
    public AsyncCommand RestoreLocalMediaCommand { get; private set; } = null!;
    public AsyncCommand VerifyLocalMediaCommand { get; private set; } = null!;

    private void InitializeMediaCommands()
    {
        SaveSettingsCommand = Command(async ct =>
        {
            try
            {
                MediaSettingsView settings;
                if (IsLocalMedia)
                {
                    if (!ConfirmLocalMediaChange("접속한 호스트의 영상 서버 비밀번호를 변경합니다.\n같은 호스트를 사용하는 모든 PC에 적용되며, 적용 확인 전까지 영상 처리를 잠시 중지합니다.\n서버가 새 설정을 읽지 못하면 MediaMTX 재시작이 필요합니다. 계속할까요?")) return;
                    _playEpoch++; await StopPlaybackAsync();
                    settings = await _client!.Post<MediaSettingsView>("/api/media/local/passwords",
                        new ChangeLocalMediaPasswordsRequest(Generation, _mediaDraftVersion, ReadApiPassword(), ReadHlsPassword()), ct);
                }
                else settings = await _client!.Post<MediaSettingsView>("/api/media/settings",
                    new SaveMediaSettingsRequest(Generation, _mediaDraftVersion, ApiEndpoint, HlsEndpoint, ApiUser, HlsUser,
                        ReadApiPassword(), ReadHlsPassword()), ct);
                ct.ThrowIfCancellationRequested(); ApplyMediaSettings(settings); CopyMediaSettingsToEditor();
                PublishStatus(IsLocalMedia ? LocalMediaMessage : "호스트 영상 설정 저장 완료 · 모든 PC에서 저장된 설정을 자동으로 사용합니다."); Raise();
            }
            finally { if (!ct.IsCancellationRequested) ClearMediaSecrets(); }
        }, () => CanEditMediaPasswords);
        RetryLocalMediaCommand = Command(ct => LocalMediaAction("retry", ct), () => CanManageMedia && HasLocalMediaChange);
        RestoreLocalMediaCommand = Command(async ct =>
        {
            if (ConfirmLocalMediaChange("접속한 호스트의 영상 서버를 변경 전 비밀번호로 복구합니다.\n같은 호스트를 사용하는 모든 PC에 적용됩니다. 복구 후 적용 확인이 필요하며, 서버가 설정을 읽지 못하면 MediaMTX를 재시작해야 합니다. 계속할까요?"))
                await LocalMediaAction("restore", ct);
        }, () => CanManageMedia && HasLocalMediaChange);
        VerifyLocalMediaCommand = Command(ct => LocalMediaAction("verify", ct), () => CanManageMedia &&
            _settings.LocalServer?.Phase is LocalMediaChangePhase.AwaitingVerification or LocalMediaChangePhase.AwaitingRestoreVerification);
    }
    private async Task LocalMediaAction(string action, CancellationToken ct)
    {
        var settings = await _client!.Post<MediaSettingsView>("/api/media/local/" + action,
            new LocalMediaActionRequest(Generation, _settings.Version, _settings.LocalServer!.ChangeId!.Value), ct,
            timeoutMs: action == "verify" ? 40000 : 8000);
        ct.ThrowIfCancellationRequested(); ApplyMediaSettings(settings); CopyMediaSettingsToEditor();
        PublishStatus(HasLocalMediaChange ? LocalMediaMessage : "호스트 영상 서버 API/HLS 인증 적용 확인 완료 · 카메라를 다시 동기화합니다.");
        ClearMediaSecrets(); Raise();
    }
    private void ApplyMediaSettings(MediaSettingsView settings)
    {
        _settings = settings; _mediaLoaded = true; _mediaReadFailed = false;
        if (!_mediaEditorOpen) CopyMediaSettingsToEditor();
    }
    private void CopyMediaSettingsToEditor()
    {
        _mediaDraftVersion = _settings.Version;
        ApiEndpoint = _settings.ApiEndpoint; HlsEndpoint = _settings.HlsEndpoint;
        ApiUser = _settings.ApiUser; HlsUser = _settings.HlsUser;
    }
    private void ResetMediaSettings()
    {
        _settings = new(0, "", "", "", "", false);
        _mediaLoaded = false; _mediaReadFailed = false; _mediaEditorOpen = false;
        CopyMediaSettingsToEditor(); ClearMediaSecrets();
    }
    private void NotifyMediaSettings()
    {
        foreach (var name in new[] { nameof(IsLocalMedia), nameof(HasLocalMediaChange), nameof(CanEditMediaTarget),
            nameof(CanEditMediaPasswords), nameof(LocalMediaMessage), nameof(SaveMediaLabel), nameof(HasMediaSettings),
            nameof(CanManageMedia), nameof(IsMediaEditorOpen), nameof(MediaStatus), nameof(MediaDescription), nameof(AppliedMedia),
            nameof(MediaEditorMessage), nameof(MediaEditorLabel), nameof(MediaDraftConflict) }) Changed(name);
    }
}
