using System.ComponentModel;
using System.Windows;

namespace IntegratedContro.App;

public partial class LoginWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<string> _previousRead;
    private readonly Action _previousClear;
    public LoginWindow(MainViewModel viewModel)
    {
        InitializeComponent();
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
        Loaded += (_, _) => { if (string.IsNullOrWhiteSpace(viewModel.Endpoint)) HostEndpoint.Focus(); else LoginNameInput.Focus(); };
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel.IsLoggedIn && !_viewModel.IsBusy && IsVisible) DialogResult = true;
    }
}
