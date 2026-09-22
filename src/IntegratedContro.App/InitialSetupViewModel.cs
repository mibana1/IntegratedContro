using System.IO;

namespace IntegratedContro.App;

public sealed class InitialSetupViewModel : Bindable
{
    private readonly StartupConfiguration _configuration;
    private readonly string _profilePath;
    private ClientPreferences _profile;
    private int _mode;
    private bool _busy;
    private string _message = "", _dataPath = "", _site = "", _admin = "", _endpoint = "", _fingerprint = "";
    public InitialSetupViewModel(StartupConfiguration configuration, string profilePath)
    {
        _configuration = configuration; _profilePath = profilePath;
        _profile = ClientPreferences.ReadForStartup(profilePath).Preferences;
        Reload();
        SaveCommand = new AsyncCommand(Save, () => !IsBusy);
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
    public int ModeIndex { get => _mode; set { if (Set(ref _mode, value)) { Changed(nameof(IsNew)); Changed(nameof(IsLocal)); Changed(nameof(IsRemote)); Changed(nameof(SaveLabel)); } } }
    public bool IsNew => ModeIndex == 0;
    public bool IsLocal => ModeIndex != 2;
    public bool IsRemote => ModeIndex == 2;
    public string SaveLabel => IsNew ? "새 서버 생성 · 설정 저장" : "선택한 설정 저장";
    public string DataPath { get => _dataPath; set => Set(ref _dataPath, value); }
    public string SiteName { get => _site; set => Set(ref _site, value); }
    public string Administrator { get => _admin; set => Set(ref _admin, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string Fingerprint { get => _fingerprint; set => Set(ref _fingerprint, value); }
    public string BindAddress { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "7443";
    public bool MediaEnabled { get; set; }
    public string MediaConfiguration { get; set; } = "";
    public string MediaApi { get; set; } = "http://127.0.0.1:9997";
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(CanEdit)); SaveCommand?.Raise(); RestoreBackupCommand?.Raise(); } } }
    public bool CanEdit => !IsBusy;
    public string Message { get => _message; private set => Set(ref _message, value); }
    public Func<string> ReadPassword { get; set; } = () => "";
    public Func<string> ReadConfirmation { get; set; } = () => "";
    public Action ClearPasswords { get; set; } = () => { };
    public event Action? Saved;
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RestoreBackupCommand { get; }
    private void Reload()
    {
        var profile = ClientPreferences.ReadForStartup(_profilePath); _profile = profile.Preferences;
        Status = _configuration.Inspect(profile); Changed(nameof(Status));
        Endpoint = _profile.Endpoint; Fingerprint = _profile.Fingerprint;
        DataPath = _configuration.DefaultDataPath;
        ModeIndex = Status.State == InitialSetupState.NewInstallation ? 0 : Status.State == InitialSetupState.RemoteServer ? 2 : 1;
        try
        {
            if (_configuration.Read() is { Enabled: true } settings)
            {
                DataPath = settings.HostDataPath; MediaEnabled = settings.MediaMtxEnabled;
                MediaConfiguration = settings.MediaMtxConfigurationPath; MediaApi = settings.MediaMtxApiEndpoint;
                foreach (var name in new[] { nameof(MediaEnabled), nameof(MediaConfiguration), nameof(MediaApi) }) Changed(name);
            }
        }
        catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e)) { Message = StartupConfiguration.FriendlyError(e); }
    }
    private async Task Save()
    {
        IsBusy = true; Message = "선택한 설정과 데이터 폴더를 확인하고 있습니다…";
        try
        {
            var service = new InitialSetupService(_configuration, _profilePath);
            if (IsRemote) service.SaveRemote(Endpoint, Fingerprint, _profile);
            else await service.SaveLocalAsync(DataPath, IsNew, SiteName, Administrator, ReadPassword(), ReadConfirmation(),
                BindAddress.Trim(), Port.Trim(), MediaEnabled, MediaConfiguration, MediaApi, _profile);
            Message = "설정을 저장했습니다."; Saved?.Invoke();
        }
        catch (Exception e) when (StartupConfiguration.IsConfigurationFailure(e))
        {
            // Setup may have created data before a later save failed. Never offer creation over it again.
            Status = _configuration.Inspect(ClientPreferences.ReadForStartup(_profilePath)); Changed(nameof(Status));
            if (IsNew && (File.Exists(Path.Combine(DataPath, "control.sqlite")) || File.Exists(Path.Combine(DataPath, "host.json")))) ModeIndex = 1;
            Message = StartupConfiguration.FriendlyError(e);
        }
        finally { ClearPasswords(); IsBusy = false; }
    }
}
