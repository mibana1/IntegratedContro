using System.IO;
using System.Text.Json;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    public InitialSetupStatus InitialSetup { get; private set; } = new(InitialSetupState.NewInstallation, "초기 설정 필요", "");
    public void RefreshInitialSetup()
    {
        try { InitialSetup = StartupConfiguration.ForApp().Inspect(ClientPreferences.ReadForStartup()); }
        catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e))
        { InitialSetup = new(InitialSetupState.ConfigurationError, "설정 오류 · 확인 필요", StartupConfiguration.FriendlyError(e)); }
        Changed(nameof(InitialSetup));
    }
    internal void ReportServerStartup(string message)
    {
        Message = HasPreferencesRecovery ? PreferencesRecoveryMessage :
            string.IsNullOrWhiteSpace(message) ? "앱 계정으로 로그인하세요." : message;
        if (message.StartsWith("서버 설정 오류:", StringComparison.Ordinal) ||
            message.StartsWith("서버 시작 확인:", StringComparison.Ordinal) || message.Contains("ControlHost:", StringComparison.Ordinal))
        {
            InitialSetup = new(InitialSetupState.ConfigurationError, "서버 준비 실패 · 확인 필요", message);
            Changed(nameof(InitialSetup));
        }
    }
    private bool _editingConnectionSettings;
    private ClientPreferencesLoadResult? _preferencesLoad;
    private string _connectionEndpoint = "", _connectionFingerprint = "", _connectionSettingsMessage = "";
    public bool HasPreferencesRecovery => _preferencesLoad?.RecoveryRequired == true;
    public bool HasPreferencesBackup => _preferencesLoad?.Backup is not null;
    public string PreferencesRecoveryMessage => _preferencesLoad?.Message ?? "";
    public string PreferencesBackupSummary => _preferencesLoad?.Backup is not { } backup ? "복구할 정상 백업 없음" :
        string.IsNullOrEmpty(backup.Endpoint) ? "백업: 접속 정보 미설정" : $"백업 호스트: {backup.Endpoint}";
    public bool IsEditingConnectionSettings
    {
        get => _editingConnectionSettings;
        private set
        {
            if (!Set(ref _editingConnectionSettings, value)) return;
            Changed(nameof(IsLoginPage)); Changed(nameof(LoginPageTitle));
            LoginCommand?.Raise();
        }
    }
    public bool IsLoginPage => !IsEditingConnectionSettings;
    public string LoginPageTitle => IsEditingConnectionSettings ? "접속 설정" : "앱 로그인";
    public string LoginHostSummary => string.IsNullOrWhiteSpace(Endpoint) || string.IsNullOrWhiteSpace(Fingerprint)
        ? "접속 설정에서 호스트 주소와 인증서 지문을 먼저 저장하세요." : $"접속 호스트: {Endpoint}";
    public string ConnectionEndpoint
    {
        get => _connectionEndpoint;
        set { if (Set(ref _connectionEndpoint, value)) ConnectionSettingsMessage = ""; }
    }
    public string ConnectionFingerprint
    {
        get => _connectionFingerprint;
        set { if (Set(ref _connectionFingerprint, value)) ConnectionSettingsMessage = ""; }
    }
    public string ConnectionSettingsMessage { get => _connectionSettingsMessage; private set => Set(ref _connectionSettingsMessage, value); }
    public AsyncCommand OpenConnectionSettingsCommand { get; private set; } = null!;
    public AsyncCommand CancelConnectionSettingsCommand { get; private set; } = null!;
    public AsyncCommand SaveConnectionSettingsCommand { get; private set; } = null!;
    public AsyncCommand RestorePreferencesBackupCommand { get; private set; } = null!;
    public AsyncCommand ReloadPreferencesCommand { get; private set; } = null!;
    private void InitializeLoginSettings(ClientPreferencesLoadResult initial)
    {
        OpenConnectionSettingsCommand = Command(() =>
        {
            ConnectionEndpoint = Endpoint; ConnectionFingerprint = Fingerprint;
            ConnectionSettingsMessage = ""; IsEditingConnectionSettings = true;
            return Task.CompletedTask;
        }, () => !IsLoggedIn);
        CancelConnectionSettingsCommand = Command(() =>
        {
            IsEditingConnectionSettings = false;
            return Task.CompletedTask;
        }, () => !IsLoggedIn);
        SaveConnectionSettingsCommand = Command(() =>
        {
            try
            {
                var endpoint = ConnectionEndpoint.Trim();
                var fingerprint = ConnectionFingerprint.Replace(" ", "").Replace(":", "").Trim().ToUpperInvariant();
                // Use the same HTTPS/pin validation as login; saving does not contact the host.
                using var validation = new HostClient(endpoint, fingerprint);
                var preferences = _preferences with { Endpoint = endpoint, Fingerprint = fingerprint };
                preferences.Save();
                _preferences = preferences; Endpoint = endpoint; Fingerprint = fingerprint;
                UpdatePreferencesRecovery(new(preferences));
                Changed(nameof(Endpoint)); Changed(nameof(Fingerprint)); Changed(nameof(LoginHostSummary)); NotifyMyInfo(); RefreshInitialSetup();
                IsEditingConnectionSettings = false;
                Message = "접속 설정을 저장했습니다. 앱 계정으로 로그인하세요.";
            }
            catch (ArgumentException e) { ConnectionSettingsMessage = e.Message; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { ConnectionSettingsMessage = "접속 설정을 저장하지 못했습니다. 앱 설정 폴더의 접근 권한과 저장 공간을 확인하세요. 기존 파일은 유지됩니다."; }
            return Task.CompletedTask;
        }, () => !IsLoggedIn && IsEditingConnectionSettings);
        RestorePreferencesBackupCommand = Command(() =>
        {
            try
            {
                ApplyClientPreferences(new(ClientPreferences.RestoreBackup()));
                Message = "백업 접속 설정을 복구했습니다. 호스트 주소를 확인하고 로그인하세요.";
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { ConnectionSettingsMessage = "백업을 복구하지 못했습니다. 설정을 다시 읽거나 주소와 지문을 입력해 저장하세요. 기존 파일은 유지됩니다."; IsEditingConnectionSettings = true; }
            return Task.CompletedTask;
        }, () => !IsLoggedIn && HasPreferencesRecovery && HasPreferencesBackup);
        ReloadPreferencesCommand = Command(() =>
        {
            ApplyClientPreferences(ClientPreferences.ReadForStartup());
            Message = HasPreferencesRecovery ? PreferencesRecoveryMessage : "접속 설정을 다시 읽었습니다. 호스트 주소를 확인하고 로그인하세요.";
            return Task.CompletedTask;
        }, () => !IsLoggedIn);
        ApplyClientPreferences(initial);
        RefreshInitialSetup();
    }
    private void ApplyClientPreferences(ClientPreferencesLoadResult result)
    {
        _preferences = result.Preferences;
        Endpoint = _preferences.Endpoint; Fingerprint = _preferences.Fingerprint;
        DeviceSettings.SetDefaultPc(_preferences.PcId); LoginName = _preferences.LastLoginName;
        ConnectionEndpoint = Endpoint; ConnectionFingerprint = Fingerprint; ConnectionSettingsMessage = "";
        UpdatePreferencesRecovery(result);
        IsEditingConnectionSettings = result.RecoveryRequired;
        if (result.RecoveryRequired) Message = result.Message;
        foreach (var name in new[] { nameof(Endpoint), nameof(Fingerprint), nameof(LoginName), nameof(LoginHostSummary) }) Changed(name);
        NotifyMyInfo();
        RefreshInitialSetup();
    }
    private void UpdatePreferencesRecovery(ClientPreferencesLoadResult result)
    {
        _preferencesLoad = result;
        foreach (var name in new[] { nameof(HasPreferencesRecovery), nameof(HasPreferencesBackup), nameof(PreferencesRecoveryMessage), nameof(PreferencesBackupSummary) }) Changed(name);
        RestorePreferencesBackupCommand?.Raise(); ReloadPreferencesCommand?.Raise(); LoginCommand?.Raise();
    }
}
