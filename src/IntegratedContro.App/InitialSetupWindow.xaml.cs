using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace IntegratedContro.App;

public partial class InitialSetupWindow : Window
{
    public InitialSetupViewModel Model { get; }
    public InitialSetupWindow(StartupConfiguration? configuration = null, string? profilePath = null)
    {
        InitializeComponent();
        Model = new(configuration ?? StartupConfiguration.ForApp(), profilePath ?? ClientPreferences.ProfilePath);
        DataContext = Model;
        Model.ReadPassword = () => AdminPassword.Password;
        Model.ReadConfirmation = () => ConfirmPassword.Password;
        Model.ClearPasswords = () => { AdminPassword.Clear(); ConfirmPassword.Clear(); };
        Model.Saved += () => Dispatcher.InvokeAsync(() => DialogResult = true);
        Closing += OnClosing;
        Closed += (_, _) => Model.ClearPasswords();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (Model.IsBusy) e.Cancel = true; }
    private void BrowseData(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "운영 데이터 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(this) == true) Model.DataPath = dialog.FolderName;
    }
}
