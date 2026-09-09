using System.Windows;
namespace IntegratedContro.App;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { new MainWindow().Show(); }
        catch (Exception error)
        {
            MessageBox.Show($"앱 시작 설정을 확인하세요. {error.Message}", "IntegratedContro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
