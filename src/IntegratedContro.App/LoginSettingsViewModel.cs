using System.IO;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    private bool _editingConnectionSettings;
    private string _connectionEndpoint = "", _connectionFingerprint = "", _connectionSettingsMessage = "";
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
    private void InitializeLoginSettings()
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
                Changed(nameof(Endpoint)); Changed(nameof(Fingerprint)); Changed(nameof(LoginHostSummary)); NotifyMyInfo();
                IsEditingConnectionSettings = false;
                Message = "접속 설정을 저장했습니다. 앱 계정으로 로그인하세요.";
            }
            catch (ArgumentException e) { ConnectionSettingsMessage = e.Message; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { ConnectionSettingsMessage = "접속 설정을 저장하지 못했습니다. 앱 설정 폴더의 접근 권한과 저장 공간을 확인하세요."; }
            return Task.CompletedTask;
        }, () => !IsLoggedIn && IsEditingConnectionSettings);
    }
}
