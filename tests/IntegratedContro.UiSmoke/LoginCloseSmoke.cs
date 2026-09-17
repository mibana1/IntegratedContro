using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunLoginClose()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
        Directory.CreateDirectory(output);
        foreach (var mode in new[] { "close", "cancel", "pending", "after-logout" })
        {
            var window = new MainWindow();
            var vm = (MainViewModel)window.DataContext;
            using var stalled = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                window.Show(); await Wait(() => window.LoginDialog?.IsVisible == true);
                var dialog = window.LoginDialog!;
                if (mode == "after-logout")
                {
                    vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint; vm.LoginName = "admin";
                    ((PasswordBox)dialog.FindName("LoginPassword")).Password = host.Password;
                    await Click(vm, (Button)dialog.FindName("ConnectButton"));
                    await Wait(() => window.LoginDialog is null);
                    Require(window.IsVisible && vm.IsLoggedIn, "Successful login closed the application");
                    await Execute(vm, vm.LogoutCommand);
                    await Wait(() => window.LoginDialog?.IsVisible == true);
                    dialog = window.LoginDialog!;
                }
                if (mode == "pending")
                {
                    // An isolated TCP listener deliberately leaves TLS negotiation pending.
                    stalled.Start();
                    vm.Endpoint = $"https://127.0.0.1:{((IPEndPoint)stalled.LocalEndpoint).Port}";
                    vm.Fingerprint = new string('0', 64); vm.LoginName = "admin";
                    ((PasswordBox)dialog.FindName("LoginPassword")).Password = "fixture";
                    vm.LoginCommand.Execute(null);
                    Require(vm.IsBusy, "Login request was not pending");
                }
                if (mode == "cancel") dialog.DialogResult = false;
                else dialog.Close();
                await Wait(() => !window.IsVisible && window.LoginDialog is null, 3000);
                Require(!vm.LoginCommand.CanExecute(null), $"Login cancellation left the app active: {mode}");
                Require(host.Process is { HasExited: false }, "Closing the UI stopped the host");
            }
            finally
            {
                stalled.Stop();
                await vm.CloseAsync();
                window.LoginDialog?.Close(); window.Close();
                await Wait(() => !window.IsVisible);
            }
        }

        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--profile-dir"); start.ArgumentList.Add(Path.Combine(output, "login-close-child"));
        start.ArgumentList.Add("--login-close-child");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Login process probe failed");
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(child.ExitCode == 0, "Production App stayed alive after closing its login dialog");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        await File.WriteAllTextAsync(Path.Combine(output, "login-close-result.txt"),
            "PASS: startup close, cancel, pending login cancellation, after-logout close, successful login keeps main window, separate host remains alive, production App exits its process naturally.\n");
        Console.WriteLine("PASS: closing/cancelling login shuts down the UI, including a pending connection; successful login keeps the app open.");
    }

    private static int RunLoginCloseChild(bool requireRecovery = false)
    {
        var app = new IntegratedContro.App.App();
        app.InitializeComponent();
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Wait(() => app.MainWindow is MainWindow { LoginDialog.IsVisible: true });
                if (requireRecovery) Require(((MainViewModel)app.MainWindow.DataContext).IsEditingConnectionSettings, "Damaged profile did not open settings");
                ((MainWindow)app.MainWindow).LoginDialog!.Close();
                await Task.Delay(3000);
                app.Shutdown(1); // Only reached if the production last-window shutdown failed.
            }
            catch { app.Shutdown(1); }
        });
        return app.Run();
    }
}