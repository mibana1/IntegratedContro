using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunManagement()
    {
        await using var host = new HostProcess(); await host.Initialize(3);
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext;
        var accounts = vm.AccountManagement; var recovery = vm.Recovery;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();
            var name = FindAll<TextBox>(window).Single(t => ReferenceEquals(t.DataContext, accounts) &&
                t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(accounts.NewAccountName));
            name.SetCurrentValue(TextBox.TextProperty, "management-operator"); name.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var password = (PasswordBox)window.FindName("NewAccountPassword"); password.Password = host.Password;
            Button Action(AsyncCommand command) => FindAll<Button>(window).Single(b => ReferenceEquals(b.Command, command));
            await Click(vm, Action(accounts.CreateAccountCommand));
            Require(password.Password == "" && accounts.Accounts.Any(a => a.Name == "management-operator"), "Account form did not save or clear the password");
            var grid = FindAll<DataGrid>(window).Single(g => ReferenceEquals(g.ItemsSource, accounts.Accounts));
            grid.SetCurrentValue(DataGrid.SelectedItemProperty, accounts.Accounts.Single(a => a.Name == "management-operator"));
            await Click(vm, Action(accounts.LoadAccountCommand));
            var enabled = FindAll<CheckBox>(window).Single(c => ReferenceEquals(c.DataContext, accounts) &&
                c.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path?.Path == nameof(accounts.AccountEnabled));
            enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Click(vm, Action(accounts.UpdateAccountCommand));
            Require(accounts.SelectedAccount is { Enabled: false } && !accounts.AccountEnabled, "Account permission binding did not update the selected account");
            await Execute(vm, vm.ReleaseCommand);
            Require(!Action(accounts.CreateAccountCommand).IsEnabled && !Action(accounts.UpdateAccountCommand).IsEnabled, "Account writes remained enabled without the lease");

            var (other, _) = await host.Login(); await HostProcess.Post<Lease>(other, "/api/lease/acquire");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.DataContext, recovery)); window.UpdateLayout();
            await Wait(() => recovery.ReviewCommand.CanExecute(null), 15000);
            Require(!Action(recovery.ApproveCommand).IsEnabled, "Recovery approval was enabled before reviewing work");
            await Click(vm, Action(recovery.ReviewCommand));
            Require(recovery.ReviewText.Contains("확인 시각") && Action(recovery.ApproveCommand).IsEnabled, "Recovery review was not connected to its own ViewModel");
            var reviewText = FindAll<TextBox>(window).Single(t => t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(recovery.ReviewText));
            Require(reviewText.Text == recovery.ReviewText, "Recovery details binding did not refresh");
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            Capture(window, Path.Combine(output, "management-recovery.png"));
            await Click(vm, Action(recovery.ApproveCommand));
            Require(vm.AcquireCommand.CanExecute(null) && !recovery.ApproveCommand.CanExecute(null), "Recovery approval did not return the shared lease to free");
            password.Password = "fixture-unsaved-password";
            await Execute(vm, vm.LogoutCommand);
            Require(accounts.Accounts.Count == 0 && accounts.SelectedAccount is null && password.Password == "" &&
                recovery.Audit.Count == 0 && vm.DeviceSettings.Devices.Count == 0 && vm.JobManagement.Jobs.Count == 0,
                "Logout retained feature state or password input");
            Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Management binding errors: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "management-result.txt"),
                "PASS: independent account form bindings, password clearing, account selection/load/permission update, lease gating; actual heartbeat loss -> recovery review -> approval; review text and audit binding; logout clears feature state. Isolated local HTTPS host only.");
            Console.WriteLine("PASS: account management and recovery ViewModels through real WPF bindings and isolated HTTPS host.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
