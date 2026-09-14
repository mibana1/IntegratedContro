using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunStorage()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var (client, _) = await host.Login();
        // Audit history crosses a page boundary without creating equipment or sending device commands.
        for (var i = 0; i < 55; i++)
        {
            var lease = await HostProcess.Post<Lease>(client, "/api/lease/acquire");
            await HostProcess.Post<Lease>(client, "/api/lease/release", new LeaseRequest(lease.Generation));
        }
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("StorageTab");
            await Click(vm, (Button)window.FindName("LoadHistory"));
            Require(vm.HistoryRows.Count == 50 && vm.OlderHistoryCommand.CanExecute(null), "Stored audit pagination missing");
            var ids = vm.HistoryRows.Select(r => r.Sequence).ToArray();
            var grid = (DataGrid)window.FindName("StoredHistoryGrid"); grid.SelectedItem = vm.HistoryRows[0];
            Require(vm.HistoryDetails.Contains("이벤트 코드"), "History selection did not show source details");
            await Click(vm, (Button)window.FindName("OlderHistory"));
            Require(vm.HistoryRows.Count == 50 && !vm.HistoryRows.Any(r => ids.Contains(r.Sequence)), "History cursor repeated rows");
            vm.HistoryKind = "명령·시나리오"; await Click(vm, (Button)window.FindName("LoadHistory"));
            Require(vm.HistoryRows.Count == 0, "Empty command history fabricated rows");
            vm.HistoryKind = "감사 기록"; await Click(vm, (Button)window.FindName("LoadHistory"));
            await Click(vm, (Button)window.FindName("CreateBackup"));
            Require(vm.BackupSummary.StartsWith("백업·검증 완료"), "Admin backup failed: " + vm.BackupSummary);
            Require(Directory.GetFiles(Path.Combine(host.DataPath, "backups"), "manifest.json", SearchOption.AllDirectories).Length == 1,
                "Verified backup completion manifest absent");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "storage-foundation.png"));
            await Execute(vm, vm.AcquireCommand);
            vm.NewAccountName = "history-viewer"; vm.NewAccountRole = AccountRole.Viewer; vm.ReadNewPassword = () => host.Password;
            await Execute(vm, vm.CreateAccountCommand); await Execute(vm, vm.LogoutCommand);
            Require(vm.HistoryRows.Count == 0, "Logout retained stored history");
            vm.LoginName = "history-viewer"; await Execute(vm, vm.LoginCommand);
            Require(!vm.BackupCommand.CanExecute(null), "Viewer could create backup");
            await Click(vm, (Button)window.FindName("LoadHistory"));
            Require(vm.HistoryRows.Count == 50, "Viewer cannot read allowed history");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Storage UI binding errors: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "storage-foundation-result.txt"),
                "PASS: real HTTPS host; cursor pagination without duplicate rows; row details; empty histories; online backup+manifest; logout clears rows; viewer read allowed and backup denied; 1180x860 WPF. Isolated data only.");
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible);
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
