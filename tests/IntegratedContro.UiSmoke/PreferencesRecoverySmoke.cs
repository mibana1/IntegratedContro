using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunPreferencesRecovery()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var path = ClientPreferences.ProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var expected = new ClientPreferences(Guid.NewGuid(), host.Endpoint, host.Fingerprint) { LastLoginName = "admin" };
        var json = JsonSerializer.Serialize(expected, JsonDefaults.Options);
        const string damaged = "{ interrupted profile";
        await File.WriteAllTextAsync(path, damaged);
        await File.WriteAllTextAsync(path + ".bak", json);
        MainWindow? window = null;
        async Task CloseWindow()
        {
            if (window is null) return;
            await ((MainViewModel)window.DataContext).CloseAsync();
            window.LoginDialog?.Close(); window.Close();
            await Wait(() => !window.IsVisible); window = null;
        }
        async Task<(MainViewModel Vm, LoginWindow Dialog)> OpenWindow()
        {
            window = new MainWindow(); window.Show();
            await Wait(() => window.LoginDialog?.IsVisible == true);
            return ((MainViewModel)window.DataContext, window.LoginDialog!);
        }
        try
        {
            var (vm, dialog) = await OpenWindow();
            Require(vm.IsEditingConnectionSettings && !vm.LoginCommand.CanExecute(null) &&
                ((FrameworkElement)dialog.FindName("PreferencesRecoveryPanel")).IsVisible,
                "Damaged profile did not open the recovery/settings screen");
            Require(await File.ReadAllTextAsync(path) == damaged && await File.ReadAllTextAsync(path + ".bak") == json,
                "Startup changed the damaged original or backup without a recovery action");
            Require(vm.PcIdText == expected.PcId.ToString() && vm.ConnectionEndpoint == expected.Endpoint &&
                ((Button)dialog.FindName("RestorePreferencesBackup")).IsEnabled,
                "Valid backup did not populate a reviewable recovery choice");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
            Capture(dialog, Path.Combine(output, "preferences-recovery-backup.png"));
            await Click(vm, (Button)dialog.FindName("RestorePreferencesBackup"));
            Require(!vm.IsEditingConnectionSettings && vm.LoginCommand.CanExecute(null) && ClientPreferences.Load() == expected,
                "Backup recovery failed to preserve PC identity, connection and recent login");
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "client.json.damaged-*.json")
                .Any(p => File.ReadAllText(p) == damaged), "Recovery discarded the damaged original");
            ((PasswordBox)dialog.FindName("LoginPassword")).Password = host.Password;
            await Click(vm, (Button)dialog.FindName("ConnectButton"));
            Require(vm.IsLoggedIn && !File.ReadAllText(path).Contains(host.Password), "Recovered login failed or stored a password");
            await CloseWindow();

            await File.WriteAllTextAsync(path, "null");
            await File.WriteAllTextAsync(path + ".bak", "{ also damaged");
            (vm, dialog) = await OpenWindow();
            Require(vm.IsEditingConnectionSettings && !((Button)dialog.FindName("RestorePreferencesBackup")).IsEnabled,
                "Invalid backup was offered for recovery");
            var newPcId = vm.PcIdText;
            ((TextBox)dialog.FindName("HostEndpoint")).Text = host.Endpoint;
            ((TextBox)dialog.FindName("CertificateFingerprint")).Text = host.Fingerprint;
            await Click(vm, (Button)dialog.FindName("SaveConnectionSettings"));
            Require(!vm.IsEditingConnectionSettings && ClientPreferences.Load().PcId.ToString() == newPcId &&
                ClientPreferences.Load().Endpoint == host.Endpoint, "Manual settings recovery did not persist");
            await CloseWindow();

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                (vm, dialog) = await OpenWindow();
                Require(vm.IsEditingConnectionSettings, "Locked profile prevented settings recovery");
                ((TextBox)dialog.FindName("HostEndpoint")).Text = host.Endpoint;
                ((TextBox)dialog.FindName("CertificateFingerprint")).Text = host.Fingerprint;
                await Click(vm, (Button)dialog.FindName("SaveConnectionSettings"));
                Require(vm.IsEditingConnectionSettings && vm.ConnectionSettingsMessage.Contains("저장하지 못"),
                    "Failed save closed settings or reported success");
                Capture(dialog, Path.Combine(output, "preferences-recovery-save-failure.png"));
            }
            await Click(vm, (Button)dialog.FindName("ReloadPreferences"));
            Require(!vm.IsEditingConnectionSettings && vm.PcIdText == newPcId && ClientPreferences.Load().PcId.ToString() == newPcId,
                "Retry after unlocking did not restore the unchanged profile");
            await CloseWindow();

            var childProfile = Path.Combine(output, "preferences-startup-child");
            Directory.CreateDirectory(childProfile); await File.WriteAllTextAsync(Path.Combine(childProfile, "client.json"), damaged);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--profile-dir"); start.ArgumentList.Add(childProfile); start.ArgumentList.Add("--preferences-startup-child");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Startup recovery probe failed");
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Require(child.ExitCode == 0, "Production App failed to start and close its recovery screen");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            await File.WriteAllTextAsync(Path.Combine(output, "preferences-recovery-result.txt"),
                "PASS: damaged profile opens real settings without modifying original; backup restore preserves PC ID/host/pin/recent ID; recovered login; invalid backup/manual recovery; locked profile starts and failed save keeps settings open; reload after unlock; real App startup/close with corrupt profile. Isolated profile/HTTPS host.");
            Console.WriteLine("Client preferences recovery WPF smoke PASS.");
        }
        finally { await CloseWindow(); }
    }
}