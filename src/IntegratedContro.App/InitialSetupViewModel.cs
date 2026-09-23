using System.IO;

namespace IntegratedContro.App;

public sealed class InitialSetupViewModel : Bindable
{
    private readonly StartupConfiguration _configuration;
    private readonly string _profilePath;
    private ClientPreferences _profile;
    private int _mode;
    private bool _busy, _mediaEnabled, _choosingMode;
    private int _mediaMode;
    private string _message = "", _dataPath = "", _site = "", _admin = "", _endpoint = "", _fingerprint = "";
    public InitialSetupViewModel(StartupConfiguration configuration, string profilePath, bool firstRun = false)
    {
        _configuration = configuration; _profilePath = profilePath;
        _profile = ClientPreferences.ReadForStartup(profilePath).Preferences;
        Reload();
        _choosingMode = firstRun;
        SaveCommand = new AsyncCommand(Save, () => !IsBusy && !IsChoosingMode);
        RestoreBackupCommand = new AsyncCommand(() =>
        {
            try { configuration.RestoreBackup(); Reload(); Message = "서버 설정 백업을 복구했습니다. 경로와 접속 정보를 확인하세요."; }
            catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e)) { Message = StartupConfiguration.FriendlyError(e); }
            return Task.CompletedTask;
        }, () => !IsBusy && File.Exists(configuration.SettingsPath + ".bak"));
    }
    public InitialSetupStatus Status { get; private set; } = null!;
    public string SettingsLocation => _configuration.SettingsPath;
    public string ProfileLocation => _profilePath;
    public string DefaultDataLocation => _configuration.DefaultDataPath;
    public bool IsChoosingMode
    {
        get => _choosingMode;
        private set
        {
            if (!Set(ref _choosingMode, value)) return;
            Changed(nameof(IsEditing)); SaveCommand.Raise();
        }
    }
    public bool IsEditing => !IsChoosingMode;
    public void ChooseMode(int mode)
    {
        if (IsBusy || mode is < 0 or > 2) return;
        ClearPasswords(); Message = ""; ModeIndex = mode; IsChoosingMode = false;
    }
    public void ShowChoices()
    {
        if (IsBusy) return;
        ClearPasswords(); Message = ""; IsChoosingMode = true;
    }
    public int ModeIndex { get => _mode; set { if (Set(ref _mode, value)) { ClearPasswords(); Changed(nameof(IsNew)); Changed(nameof(IsExistingData)); Changed(nameof(IsLocal)); Changed(nameof(IsRemote)); Changed(nameof(SaveLabel)); } } }
    public bool IsNew => ModeIndex == 0;
    public bool IsExistingData => ModeIndex == 1;
    public bool IsLocal => ModeIndex != 2;
    public bool IsRemote => ModeIndex == 2;
    public string SaveLabel => IsNew ? "새로 시작 · 관리자 접속" : "설정 저장 · 로그인으로";
    public string DataPath { get => _dataPath; set { if (Set(ref _dataPath, value)) ReloadMediaPorts(); } }
    public string SiteName { get => _site; set => Set(ref _site, value); }
    public string Administrator { get => _admin; set => Set(ref _admin, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string Fingerprint { get => _fingerprint; set => Set(ref _fingerprint, value); }
    public string BindAddress { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "7443";
    public bool MediaEnabled { get => _mediaEnabled; set { if (Set(ref _mediaEnabled, value)) Changed(nameof(MediaSetupSummary)); } }
    public int MediaModeIndex { get => _mediaMode; set { if (Set(ref _mediaMode, value)) { Changed(nameof(IsAutomaticMedia)); Changed(nameof(IsExistingMedia)); Changed(nameof(MediaSetupSummary)); } } }
    public bool IsAutomaticMedia => MediaModeIndex == 0;
    public bool IsExistingMedia => !IsAutomaticMedia;
    public string MediaSetupSummary => !MediaEnabled ? "영상 서버 사용이 꺼져 있습니다. 카메라 영상을 사용하려면 위 항목을 켜세요." :
        IsAutomaticMedia ? "저장 시 영상 서버 주소·계정·비밀번호를 자동 설정합니다. 로그인 후 영상 서버 화면에서 다시 입력할 필요가 없습니다." :
        "기존 영상 서버 파일을 사용합니다. 고급 설정에서 파일 경로와 접속 주소를 확인하세요.";
    public string MediaApiPort { get; set; } = "9997";
    public string MediaHlsPort { get; set; } = "8888";
    public string MediaRtspPort { get; set; } = "8554";
    private sealed record MediaSetupPorts(int ApiPort, int HlsPort, int RtspPort);
    public string MediaConfiguration { get; set; } = "";
    public string MediaApi { get; set; } = "http://127.0.0.1:9997";
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(CanEdit)); SaveCommand?.Raise(); RestoreBackupCommand?.Raise(); } } }
    public bool CanEdit => !IsBusy;
    public string Message { get => _message; private set => Set(ref _message, value); }
    public Func<string> ReadPassword { get; set; } = () => "";
    public Func<string> ReadConfirmation { get; set; } = () => "";
    public Action ClearPasswords { get; set; } = () => { };
    // The submitted password is used only for this continuation, never persisted or kept on the model.
    public Func<ClientPreferences, string?, Task>? ContinueAfterSave { get; set; }
    public event Action? Saved;
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RestoreBackupCommand { get; }
    private void ReloadMediaPorts()
    {
        if (string.IsNullOrWhiteSpace(DataPath) || !Path.IsPathFullyQualified(DataPath)) return;
        try
        {
            if (StartupConfiguration.ReadFile<MediaSetupPorts>(Path.Combine(DataPath, "MediaMTX", "setup.json")) is not { } ports) return;
            if (new[] { ports.ApiPort, ports.HlsPort, ports.RtspPort }.Any(p => p is < 1024 or > 65535))
                throw new InvalidDataException("저장된 영상 서버 포트를 확인하세요.");
            MediaApiPort = ports.ApiPort.ToString(); MediaHlsPort = ports.HlsPort.ToString(); MediaRtspPort = ports.RtspPort.ToString();
            Changed(nameof(MediaApiPort)); Changed(nameof(MediaHlsPort)); Changed(nameof(MediaRtspPort));
        }
        catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e)) { Message = StartupConfiguration.FriendlyError(e); }
    }
    private void Reload()
    {
        var profile = ClientPreferences.ReadForStartup(_profilePath); _profile = profile.Preferences;
        Status = _configuration.Inspect(profile); Changed(nameof(Status));
        Endpoint = _profile.Endpoint; Fingerprint = _profile.Fingerprint;
        DataPath = _configuration.DefaultDataPath;
        // Default only for a fresh installation; saved local choices below remain authoritative.
        MediaEnabled = Status.State == InitialSetupState.NewInstallation;
        ModeIndex = Status.State == InitialSetupState.NewInstallation ? 0 : Status.State == InitialSetupState.RemoteServer ? 2 : 1;
        try
        {
            if (_configuration.Read() is { Enabled: true } settings)
            {
                StartupConfiguration.ValidateSettings(settings);
                DataPath = settings.HostDataPath; MediaEnabled = settings.MediaMtxEnabled;
                MediaConfiguration = settings.MediaMtxConfigurationPath; MediaApi = settings.MediaMtxApiEndpoint;
                MediaModeIndex = settings.MediaMtxEnabled ? 1 : 0;
                var managed = Path.Combine(DataPath, "MediaMTX", "mediamtx.yml");
                if (Path.GetFullPath(string.IsNullOrWhiteSpace(MediaConfiguration) ? managed : MediaConfiguration).Equals(managed, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(DataPath, "MediaMTX", "setup.json")))
                    MediaModeIndex = 0;
                foreach (var name in new[] { nameof(MediaEnabled), nameof(MediaConfiguration), nameof(MediaApi) }) Changed(name);
            }
        }
        catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e)) { Message = StartupConfiguration.FriendlyError(e); }
    }
    private async Task Save()
    {
        IsBusy = true;
        string? password = IsNew ? ReadPassword() : null;
        var confirmation = IsNew ? ReadConfirmation() : "";
        var automaticMedia = IsLocal && MediaEnabled && IsAutomaticMedia;
        Message = automaticMedia ? "영상 서버 설정을 생성하고 API/HLS 인증 연결을 확인하고 있습니다…" : "선택한 설정과 데이터 폴더를 확인하고 있습니다…";
        try
        {
            ClientPreferences saved;
            try
            {
                var service = new InitialSetupService(_configuration, _profilePath);
                if (IsRemote) saved = service.SaveRemote(Endpoint, Fingerprint, _profile);
                else saved = await service.SaveLocalAsync(DataPath, IsNew, SiteName, Administrator, password ?? "", confirmation,
                    BindAddress.Trim(), Port.Trim(), MediaEnabled, MediaConfiguration, MediaApi, _profile, automaticMedia,
                    MediaApiPort.Trim(), MediaHlsPort.Trim(), MediaRtspPort.Trim());
            }
            catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e))
            {
                // Setup may have created data before a later save failed. Never offer creation over it again.
                Status = _configuration.Inspect(ClientPreferences.ReadForStartup(_profilePath)); Changed(nameof(Status));
                if (IsNew && (File.Exists(Path.Combine(DataPath, "control.sqlite")) || File.Exists(Path.Combine(DataPath, "host.json")))) ModeIndex = 1;
                Message = StartupConfiguration.FriendlyError(e);
                return;
            }
            ClearPasswords(); confirmation = "";
            Message = automaticMedia ? "영상 서버 설정·전용 계정 등록 및 API/HLS 인증 연결 확인을 완료했습니다. 카메라 영상은 등록 후 확인하세요." : "설정을 저장했습니다.";
            if (ContinueAfterSave is not null)
            {
                Message = password is null ? "설정을 저장했습니다. 서버 실행 상태를 확인하고 있습니다…" : "관리자 계정을 만들었습니다. 서버를 준비하고 접속하고 있습니다…";
                await ContinueAfterSave(saved, password);
            }
            Saved?.Invoke();
        }
        finally { password = null; confirmation = ""; ClearPasswords(); IsBusy = false; }
    }
}
