using System.Windows;
namespace IntegratedContro.App;
public partial class App : System.Windows.Application
{
    private readonly LocalServerLifetime _servers = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { new MainWindow(prepareServers: () => LocalServerStartup.StartForAppAsync(servers =>
                Dispatcher.InvokeAsync(() => MessageBox.Show(MainWindow,
                    "다음 서버가 꺼져 있습니다. 지금 실행할까요?\n\n" + string.Join("\n", servers) +
                    "\n\n이 앱을 닫으면 이 앱이 시작한 서버도 종료됩니다.", "서버 실행",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes).Task, _servers), stopServers: _servers.StopAsync).Show(); }
        catch (Exception error)
        {
            MessageBox.Show($"앱 시작 설정을 확인하세요. {error.Message}", "IntegratedContro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
