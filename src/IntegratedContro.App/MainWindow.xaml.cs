using System.ComponentModel;
using System.Windows;

namespace IntegratedContro.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closed, _closing, _wasLoggedIn;
    private readonly bool _showLoginPrompts;
    public LoginWindow? LoginDialog { get; private set; }
    public MainWindow(bool showLoginOnStart = true)
    {
        _showLoginPrompts = showLoginOnStart;
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.AccountManagement.ReadNewPassword = () => NewAccountPassword.Password;
        _viewModel.AccountManagement.ClearNewPassword = NewAccountPassword.Clear;
        _viewModel.JobManagement.ConfirmManualSwitch = text => MessageBox.Show(this, text, "시나리오 중단 후 수동 전환",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ModelChanged;
        if (showLoginOnStart) Loaded += (_, _) => Dispatcher.InvokeAsync(ShowLogin);
        Closed += (_, _) => _viewModel.PropertyChanged -= ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_viewModel.IsAdmin && MainTabs.SelectedItem == AdminTab) MainTabs.SelectedIndex = 0;
        var loggedOut = _wasLoggedIn && !_viewModel.IsLoggedIn;
        _wasLoggedIn = _viewModel.IsLoggedIn;
        if (loggedOut && !_closing && _showLoginPrompts) Dispatcher.InvokeAsync(ShowLogin);
    }
    private void OpenMyInfo(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = MyInfoTab;
    private void OpenAccountSettings(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsAdmin) MainTabs.SelectedItem = AdminTab;
    }
    private void OpenLogin(object sender, RoutedEventArgs e) => ShowLogin();
    private void ShowLogin()
    {
        if (_closed || _closing || _viewModel.IsLoggedIn || LoginDialog is not null) return;
        LoginDialog = new LoginWindow(_viewModel) { Owner = this };
        bool authenticated;
        try { authenticated = LoginDialog.ShowDialog() == true; }
        finally { LoginDialog = null; }
        if (!authenticated && !_closed && !_closing) Close();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await _viewModel.CloseAsync();
        // Even a logged-out/disconnected cleanup can complete synchronously. Exit the Closing event first.
        await System.Windows.Threading.Dispatcher.Yield();
        _closed = true; Close();
    }
}
