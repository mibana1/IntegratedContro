using System.ComponentModel;
using System.Windows;

namespace IntegratedContro.App;

public partial class LoginWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<string> _previousRead;
    private readonly Action _previousClear;
    private readonly Func<Task<string>>? _prepareServers;
    private bool _settingUp;
    public LoginWindow(MainViewModel viewModel, Func<Task<string>>? prepareServers = null)
    {
        InitializeComponent();
        _prepareServers = prepareServers;
        _viewModel = viewModel; DataContext = viewModel;
        _previousRead = viewModel.ReadLoginPassword; _previousClear = viewModel.ClearLoginPassword;
        viewModel.ReadLoginPassword = () => LoginPassword.Password;
        viewModel.ClearLoginPassword = LoginPassword.Clear;
        viewModel.PropertyChanged += ModelChanged;
        Closed += (_, _) =>
        {
            LoginPassword.Clear();
            viewModel.PropertyChanged -= ModelChanged;
            viewModel.ReadLoginPassword = _previousRead; viewModel.ClearLoginPassword = _previousClear;
        };
        Loaded += (_, _) => FocusPage();
    }
    private void OpenSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingUp = true;
            var setup = new InitialSetupWindow { Owner = this };
            setup.Model.ContinueAfterSave = (preferences, password) => _viewModel.CompleteInitialSetupAsync(preferences, password, _prepareServers);
            if (setup.ShowDialog() == true)
            {
                if (_viewModel.IsLoggedIn) DialogResult = true;
                else FocusPage();
            }
            else _viewModel.RefreshInitialSetup();
        }
        catch (Exception error) when (StartupConfiguration.IsConfigurationFailure(error))
        { _viewModel.ReportServerStartup(StartupConfiguration.FriendlyError(error)); }
        finally { _settingUp = false; }
    }
    private void FocusPage()
    {
        if (_viewModel.IsEditingConnectionSettings) HostEndpoint.Focus();
        else if (string.IsNullOrWhiteSpace(_viewModel.Endpoint) || string.IsNullOrWhiteSpace(_viewModel.Fingerprint)) OpenConnectionSettings.Focus();
        else if (string.IsNullOrWhiteSpace(_viewModel.LoginName)) LoginNameInput.Focus();
        else LoginPassword.Focus();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEditingConnectionSettings))
            Dispatcher.InvokeAsync(FocusPage);
        if (!_settingUp && _viewModel.IsLoggedIn && !_viewModel.IsBusy && IsVisible) DialogResult = true;
    }
}
