using System.ComponentModel;
using System.Windows;

namespace IntegratedContro.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closed, _closing, _wasLoggedIn, _preparingServers;
    private readonly bool _showLoginPrompts;
    private readonly Func<Task>? _stopServers;
    private readonly Func<Task<string>>? _prepareServers;
    public LoginWindow? LoginDialog { get; private set; }
    public ThemeManager Appearance { get; } = ThemeManager.Current;
    public MainWindow(bool showLoginOnStart = true, Func<Task<string>>? prepareServers = null, Func<Task>? stopServers = null)
    {
        _showLoginPrompts = showLoginOnStart;
        _stopServers = stopServers;
        _prepareServers = prepareServers;
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.AccountManagement.ReadNewPassword = () => NewAccountPassword.Password;
        _viewModel.AccountManagement.ClearNewPassword = NewAccountPassword.Clear;
        _viewModel.JobManagement.ConfirmManualSwitch = text => MessageBox.Show(this, text, "시나리오 중단 후 수동 전환",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ModelChanged;
        if (showLoginOnStart) Loaded += async (_, _) =>
        {
            if (prepareServers is not null)
            {
                _preparingServers = true;
                _viewModel.ReportServerStartup("로컬 서버 실행 상태를 확인하고 있습니다…");
                string message;
                try { message = await prepareServers(); }
                catch (Exception) { message = "서버 준비를 완료하지 못했습니다. 서버 설정과 실행 상태를 확인한 뒤 접속하세요."; }
                _preparingServers = false;
                if (_closed || _closing) return;
                _viewModel.ReportServerStartup(message);
            }
            _ = Dispatcher.InvokeAsync(ShowLogin);
        };
        Closed += (_, _) => _viewModel.PropertyChanged -= ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_viewModel.IsAdmin && MainTabs.SelectedItem == AdminTab) MainTabs.SelectedIndex = 0;
        var loggedOut = _wasLoggedIn && !_viewModel.IsLoggedIn;
        _wasLoggedIn = _viewModel.IsLoggedIn;
        if (loggedOut && !_closing && _showLoginPrompts) Dispatcher.InvokeAsync(ShowLogin);
    }
    private void OpenAccountSettings(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsAdmin) MainTabs.SelectedItem = AdminTab;
    }
    private void OpenLogin(object sender, RoutedEventArgs e) => ShowLogin();
    private void ShowLogin()
    {
        if (_closed || _closing || _preparingServers || _viewModel.IsLoggedIn || LoginDialog is not null) return;
        LoginDialog = new LoginWindow(_viewModel, _prepareServers) { Owner = this };
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
        if (_stopServers is not null)
        {
            _viewModel.ReportServerStartup("이 앱이 시작한 서버를 종료하고 있습니다…");
            try { await _stopServers(); }
            catch (Exception) { MessageBox.Show(this, "서버 종료를 완료하지 못했습니다. 서버 실행 상태를 확인하세요.", "서버 종료", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        // Even a logged-out/disconnected cleanup can complete synchronously. Exit the Closing event first.
        await System.Windows.Threading.Dispatcher.Yield();
        _closed = true; Close();
    }
}
