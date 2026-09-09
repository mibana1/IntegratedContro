using System.ComponentModel;
using System.Windows;

namespace IntegratedContro.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closed, _closing;
    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel
        {
            ReadLoginPassword = () => LoginPassword.Password,
            ClearLoginPassword = LoginPassword.Clear,
            ReadNewPassword = () => NewAccountPassword.Password,
            ClearNewPassword = NewAccountPassword.Clear,
            ConfirmManualSwitch = text => MessageBox.Show(this, text, "시나리오 중단 후 수동 전환",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes
        };
        DataContext = _viewModel;
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
