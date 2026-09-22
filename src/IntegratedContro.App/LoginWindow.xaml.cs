using System.ComponentModel;
using System.Windows;

namespace IntegratedContro.App;

public partial class LoginWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<string> _previousRead;
    private readonly Action _previousClear;
    private readonly Func<Task<string>>? _prepareServers;
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
    private async void OpenSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            var setup = new InitialSetupWindow { Owner = this };
            if (setup.ShowDialog() == true)
            {
                _viewModel.ReloadPreferencesCommand.Execute(null);
                var startup = _prepareServers is not null ? await _prepareServers() : "";
                _viewModel.ReportServerStartup(setup.Model.Message + (startup.Length == 0 ? "" : "\n" + startup));
            }
            else _viewModel.RefreshInitialSetup();
        }
        catch (Exception error) when (StartupConfiguration.IsConfigurationFailure(error))
        { _viewModel.ReportServerStartup(StartupConfiguration.FriendlyError(error)); }
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
        if (_viewModel.IsLoggedIn && !_viewModel.IsBusy && IsVisible) DialogResult = true;
    }
}
