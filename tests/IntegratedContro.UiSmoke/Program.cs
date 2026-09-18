using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

/// <summary>Runs the production WPF window/ViewModels on an STA dispatcher against a separate real HTTPS EXE.
/// This is code-driven WPF smoke coverage, not native input automation or a physical second-PC test.</summary>
public static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var profileIndex = Array.IndexOf(args, "--profile-dir");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IntegratedContro.sln"))) root = root.Parent;
        if (profileIndex < 0 || profileIndex + 1 >= args.Length || root is null ||
            !Path.GetFullPath(args[profileIndex + 1]).StartsWith(Path.Combine(root.FullName, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Use --profile-dir with a folder under repository artifacts; user preferences must not be overwritten.");
            return 2;
        }
        Directory.CreateDirectory(Path.GetFullPath(args[profileIndex + 1]));
        if (args.Contains("--theme-startup-child")) return RunThemeStartupChild();
        if (args.Contains("--login-close-child")) return RunLoginCloseChild();
        if (args.Contains("--preferences-startup-child")) return RunLoginCloseChild(requireRecovery: true);
        var app = new System.Windows.Application();
        app.Resources = new ResourceDictionary { Source = new Uri("/IntegratedContro.App;component/Theme.xaml", UriKind.Relative) }; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args.Contains("--theme-only")) await RunThemeSwitching();
                else if (args.Contains("--design-only")) await RunDesignControls();
                else if (args.Contains("--login-only")) { await RunLogin(); await RunLoginClose(); }
                else if (args.Contains("--environment-adapters-only")) await RunEnvironmentAdapters();
                else if (args.Contains("--preferences-only")) await RunPreferencesRecovery();
                else if (args.Contains("--draft-recovery-only")) await RunDraftRecovery();
                else if (args.Contains("--camera-content-lookup-only")) await RunCameraContentLookup();
                else if (args.Contains("--camera-status-only")) await RunCameraStatus();
                else if (args.Contains("--camera-input-only")) await RunCameraInput();
                else if (args.Contains("--media-only")) { await RunRelay(); await RunMedia(); await RunPreview(); }
                else if (args.Contains("--preview-only")) { await RunRelay(); await RunPreview(); }
                else if (args.Contains("--editor-only")) { await RunHiperwallEditing(); await RunHiperwallDeletion(); }
                else if (args.Contains("--deletion-only")) await RunHiperwallDeletion();
                else if (args.Contains("--scenarios-only")) await RunScenarioExtensions();
                else if (args.Contains("--scenario-settings-only")) await RunScenarioSettings();
                else if (args.Contains("--role-management-only")) { await RunRoleManagement(); await RunRoleUnassignment(); }
                else if (args.Contains("--role-unassignment-only")) await RunRoleUnassignment();
                else if (args.Contains("--lighting-only")) await RunLighting();
                else if (args.Contains("--scenario-editor-only")) await RunScenarioEditor();
                else if (args.Contains("--numeric-input-only")) await RunNumericInputs();
                else if (args.Contains("--power-input-only")) await RunPowerInputs();
                else if (args.Contains("--management-only")) await RunManagement();
                else if (args.Contains("--device-drivers-only")) await RunDeviceDriverSettings();
                else if (args.Contains("--slots-only")) await RunHiperwallSlots();
                else if (args.Contains("--layouts-only")) await RunHiperwallLayouts();
                else if (args.Contains("--handover-only")) await RunHandover();
                else
                {
                    if (!args.Contains("--hiperwall-only")) { await RunLogin(); await RunLoginClose(); await RunPreferencesRecovery(); await Run(); await RunLighting(); await RunCameraInput(); await RunCameraStatus(); await RunDraftRecovery(); }
                    await RunThemeSwitching(); await RunDesignControls(); await RunHiperwall(); await RunHiperwallEditing(); await RunHiperwallDeletion(); await RunHiperwallLayouts(); await RunHiperwallSlots(); await RunScenarioExtensions(); await RunScenarioSettings(); await RunRoleUnassignment(); await RunRoleManagement(); await RunPowerInputs(); await RunDeviceDriverSettings(); await RunNumericInputs(); await RunScenarioEditor(); await RunHandover(); await RunEnvironmentAdapters(); await RunManagement(); await RunCameraContentLookup(); await RunRelay(); await RunPreview();
                }
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run(); return result;
    }

    private static async Task RunLogin()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        var stablePcId = Guid.NewGuid();
        await File.WriteAllTextAsync(ClientPreferences.ProfilePath, System.Text.Json.JsonSerializer.Serialize(
            new { pcId = stablePcId, endpoint = "", fingerprint = "" }, JsonDefaults.Options));
        Require(ClientPreferences.ReadForStartup().Preferences.LastLoginName == "", "Legacy profile did not default to an empty recent login");
        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext; vm.Endpoint = ""; vm.Fingerprint = "";
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); await Wait(() => window.LoginDialog?.IsVisible == true);
            Require(((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Collapsed, "Admin tab visible before login");
            var dialog = window.LoginDialog!;
            Require(!((TextBox)dialog.FindName("HostEndpoint")).IsVisible && vm.LoginName == "",
                "Connection settings leaked into the login form or first-use ID was not blank");
            await Click(vm, (Button)dialog.FindName("OpenConnectionSettings")); dialog.UpdateLayout();
            Require(((TextBox)dialog.FindName("HostEndpoint")).IsVisible && !vm.LoginCommand.CanExecute(null),
                "Settings did not open separately or allowed login with unapplied settings");
            ((TextBox)dialog.FindName("HostEndpoint")).SetCurrentValue(TextBox.TextProperty, "http://127.0.0.1:8000");
            ((TextBox)dialog.FindName("CertificateFingerprint")).SetCurrentValue(TextBox.TextProperty, host.Fingerprint);
            await Click(vm, (Button)dialog.FindName("SaveConnectionSettings"));
            Require(vm.IsEditingConnectionSettings && vm.ConnectionSettingsMessage.Contains("https://") &&
                ClientPreferences.ReadForStartup().Preferences.Endpoint == "", "Invalid HTTPS settings were accepted");
            ((TextBox)dialog.FindName("HostEndpoint")).SetCurrentValue(TextBox.TextProperty, host.Endpoint);
            ((TextBox)dialog.FindName("CertificateFingerprint")).SetCurrentValue(TextBox.TextProperty, "invalid");
            await Click(vm, (Button)dialog.FindName("SaveConnectionSettings"));
            Require(vm.IsEditingConnectionSettings && vm.ConnectionSettingsMessage.Contains("SHA-256"),
                "Invalid certificate pin was accepted");
            ((TextBox)dialog.FindName("CertificateFingerprint")).SetCurrentValue(TextBox.TextProperty, host.Fingerprint.ToLowerInvariant());
            dialog.UpdateLayout(); Capture(dialog, Path.Combine(output, "login-connection-settings.png"));
            await Click(vm, (Button)dialog.FindName("SaveConnectionSettings")); dialog.UpdateLayout();
            var settings = ClientPreferences.ReadForStartup().Preferences;
            Require(!vm.IsEditingConnectionSettings && settings.Endpoint == host.Endpoint && settings.Fingerprint == host.Fingerprint &&
                settings.PcId == stablePcId && settings.LastLoginName == "", "Settings were not saved before login or changed PC identity");
            await Click(vm, (Button)dialog.FindName("OpenConnectionSettings"));
            ((TextBox)dialog.FindName("HostEndpoint")).SetCurrentValue(TextBox.TextProperty, "https://127.0.0.1:1");
            await Click(vm, (Button)dialog.FindName("CancelConnectionSettings"));
            Require(vm.Endpoint == host.Endpoint && ClientPreferences.ReadForStartup().Preferences.Endpoint == host.Endpoint, "Returning applied unsaved settings");
            ((TextBox)dialog.FindName("LoginNameInput")).SetCurrentValue(TextBox.TextProperty, "missing-fixture-account");
            var password = (PasswordBox)dialog.FindName("LoginPassword");
            password.Password = "invalid-test-password";
            await Click(vm, (Button)dialog.FindName("ConnectButton"));
            Require(!vm.IsLoggedIn && dialog.IsVisible && password.Password == "" && ClientPreferences.ReadForStartup().Preferences.LastLoginName == "",
                "Failed login closed popup, retained password or remembered a failed ID");
            Capture(dialog, Path.Combine(output, "login-popup.png")); // No password is present in the evidence.
            ((TextBox)dialog.FindName("LoginNameInput")).SetCurrentValue(TextBox.TextProperty, "admin");
            password.Password = host.Password;
            await Click(vm, (Button)dialog.FindName("ConnectButton"));
            await Wait(() => window.LoginDialog is null);
            Require(ClientPreferences.ReadForStartup().Preferences.LastLoginName == "admin", "Successful account ID was not remembered");
            Require(vm.IsLoggedIn && ((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Visible, "Admin login did not expose admin tab");
            Require(!(await File.ReadAllTextAsync(ClientPreferences.ProfilePath)).Contains(host.Password), "Password was persisted");
            await Execute(vm, vm.AcquireCommand);
            vm.AccountManagement.NewAccountName = "operator"; vm.AccountManagement.NewAccountRole = AccountRole.Operator; vm.AccountManagement.ReadNewPassword = () => host.Password;
            await Execute(vm, vm.AccountManagement.CreateAccountCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("MyInfoTab"); window.UpdateLayout();
            Require(((TabControl)window.FindName("MainTabs")).SelectedItem == window.FindName("MyInfoTab") &&
                ((TextBlock)window.FindName("MyAccountNameText")).Text == "admin" && vm.MyAccountRole == "관리자" &&
                vm.MyControlStatus == "사용 중" && vm.MySlotSupport == "지원됨", "My information did not reflect the logged-in account/host");
            Capture(window, Path.Combine(output, "my-info-admin.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "my-info-small.png"));
            await Click(vm, (Button)window.FindName("AccountSettingsButton"));
            Require(((TabControl)window.FindName("MainTabs")).SelectedItem == window.FindName("AdminTab"), "Account management link did not open settings");
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("MyInfoTab"); window.UpdateLayout();
            await Click(vm, (Button)window.FindName("HeaderLogout"));
            await Wait(() => window.LoginDialog?.IsVisible == true);
            Require(((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Collapsed &&
                ((TabControl)window.FindName("MainTabs")).SelectedItem != window.FindName("AdminTab") &&
                vm.MyAccountName == "로그인 전" && vm.MyAccountId == "—" && vm.MySessionId == "—",
                "Logout retained admin page or previous account details");
            dialog = window.LoginDialog!;
            ((TextBox)dialog.FindName("LoginNameInput")).SetCurrentValue(TextBox.TextProperty, "operator");
            ((PasswordBox)dialog.FindName("LoginPassword")).Password = host.Password;
            await Click(vm, (Button)dialog.FindName("ConnectButton")); await Wait(() => window.LoginDialog is null);
            Require(vm.IsLoggedIn && !vm.IsAdmin && ((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Collapsed,
                "Operator could see admin settings");
            Require(!vm.DeviceSettings.SaveDeviceCommand.CanExecute(null), "Hidden admin form retained write access");
            Require(vm.MyAccountName == "operator" && vm.MyAccountRole == "운영자" &&
                ((Button)window.FindName("AccountSettingsButton")).Visibility == Visibility.Collapsed,
                "My information retained administrator identity/access after account switch");
            window.UpdateLayout(); Capture(window, Path.Combine(output, "my-info-operator.png"));
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0; window.UpdateLayout();
            Capture(window, Path.Combine(output, "operator-home.png"));
            Require(ClientPreferences.ReadForStartup().Preferences.LastLoginName == "operator", "Account switch did not update recent ID");
            var reopened = new MainViewModel();
            try
            {
                Require(reopened.LoginName == "operator" && reopened.Endpoint == host.Endpoint && reopened.Fingerprint == host.Fingerprint,
                    "App restart did not restore successful ID and connection settings");
                var rememberedDialog = new LoginWindow(reopened) { Owner = window };
                try
                {
                    rememberedDialog.Show(); rememberedDialog.UpdateLayout();
                    Require(((TextBox)rememberedDialog.FindName("LoginNameInput")).Text == "operator" &&
                        ((PasswordBox)rememberedDialog.FindName("LoginPassword")).Password == "",
                        "Remembered ID was not filled or password was restored");
                    Capture(rememberedDialog, Path.Combine(output, "login-remembered-id.png"));
                    await Click(reopened, (Button)rememberedDialog.FindName("OpenConnectionSettings"));
                    await Click(reopened, (Button)rememberedDialog.FindName("SaveConnectionSettings"));
                    Require(ClientPreferences.ReadForStartup().Preferences.LastLoginName == "operator", "Saving connection settings erased the recent ID");
                    ((TextBox)rememberedDialog.FindName("LoginNameInput")).SetCurrentValue(TextBox.TextProperty, "missing-fixture-account");
                    ((PasswordBox)rememberedDialog.FindName("LoginPassword")).Password = "invalid-test-password";
                    await Click(reopened, (Button)rememberedDialog.FindName("ConnectButton"));
                    Require(!reopened.IsLoggedIn && ClientPreferences.ReadForStartup().Preferences.LastLoginName == "operator", "Failed login replaced the last successful ID");
                }
                finally { rememberedDialog.Close(); }
            }
            finally { await reopened.CloseAsync(); }
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Login binding errors: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "login-result.txt"),
                "PASS: separate connection settings, HTTPS/pin validation, save before login, unapplied edit cancellation and legacy profile/PC ID preservation; latest successful account ID survives restart and settings save while failed logins do not replace it; no password persistence; startup modal fields and actual WPF bindings; failed login retains dialog and clears password; successful login closes dialog; My information navigation, current account/host support, compact rendering and admin settings link; profile logout reopens popup and clears account IDs; operator profile cannot see admin controls; password not saved. Local code-driven WPF.");
        }
        finally
        {
            window.LoginDialog?.Close(); window.Close(); await Wait(() => !window.IsVisible);
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
    private static async Task Run()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
        Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter();
        using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        MainWindow? first = null, second = null;
        try
        {
            first = new MainWindow(false) { Title = "IntegratedContro · 로컬 WPF 검증 A" };
            var a = (MainViewModel)first.DataContext;
                        using (var badPin = new HostClient(host.Endpoint, new string('0', 64)))
            {
                var rejected = false;
                try { await badPin.Get<object>("/health"); } catch (System.Net.Http.HttpRequestException) { rejected = true; }
                Require(rejected, "WPF client accepted the wrong TLS fingerprint");
            }
            a.Endpoint = host.Endpoint; a.Fingerprint = host.Fingerprint; a.LoginName = "admin";
            a.ReadLoginPassword = () => host.Password;
            first.DataContext = null; first.DataContext = a; first.Show();
            a.DeviceViewIndex = 1; first.UpdateLayout();
            await Execute(a, a.LoginCommand); Require(a.IsLoggedIn, a.Message);
            await Execute(a, a.AcquireCommand); Require(a.CanControl, a.Message);
            a.DeviceSettings.DeviceName = "검증용 가상 조명"; a.DeviceSettings.ConnectionId = "virtual.local";
            a.DeviceSettings.SelectedModel = a.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
            await Execute(a, a.DeviceSettings.SaveDeviceCommand); Require(a.DeviceSettings.Devices.Count == 1, a.Message);
            var deviceGrid = (DataGrid)first.FindName("DeviceGrid");
            var rolePicker = (ComboBox)first.FindName("QuickRolePicker");
            var assignButton = (Button)first.FindName("QuickAssignRole");
            var controlRole = (ComboBox)first.FindName("ControlRolePicker");
            var firstDevice = a.DeviceSettings.Devices[0];
            var primaryRole = a.DeviceControl.Roles.Single();
            Require(primaryRole.IsDefault, "New device did not receive a default role");
            deviceGrid.SetCurrentValue(DataGrid.SelectedItemProperty, firstDevice);
            rolePicker.SetCurrentValue(ComboBox.SelectedItemProperty, a.DeviceSettings.RoleChoices.Single(c => c.Id == primaryRole.Id));
            var resets = 0;
            a.DeviceSettings.Devices.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
            await Execute(a, a.RefreshCommand); await Task.Delay(1300);
            Require(ReferenceEquals(firstDevice, a.DeviceSettings.SelectedDevice) && ReferenceEquals(deviceGrid.SelectedItem, firstDevice)
                && resets == 0, "Polling reset device selection");
            await Click(a, assignButton);
            Require(a.DeviceControl.SelectedRole?.Id == primaryRole.Id && controlRole.SelectedItem is RoleChoice choice && choice.Id == primaryRole.Id,
                "Inherited role not selected for control");
            first.UpdateLayout(); Capture(first, Path.Combine(output, "role-assignment.png"));

            await Execute(a, a.DeviceSettings.NewDeviceCommand);
            a.DeviceSettings.DeviceName = firstDevice.Name; a.DeviceSettings.DeviceLocation = "다른 위치";
            await Execute(a, a.DeviceSettings.SaveDeviceCommand); Require(a.DeviceSettings.Devices.Count == 2, a.Message);
            var secondDevice = a.DeviceSettings.Devices.Single(d => d.Id != firstDevice.Id);
            var adminTabs = Find<TabControl>(first)!; adminTabs.SelectedItem = first.FindName("AdminTab");
            ((Expander)first.FindName("RoleManagementExpander")).IsExpanded = true; first.UpdateLayout();
            var targetPicker = (ComboBox)first.FindName("RoleDevicePicker");
            targetPicker.SetCurrentValue(ComboBox.SelectedItemProperty, secondDevice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(a.DeviceSettings.SelectedDevice?.Id == secondDevice.Id && a.DeviceSettings.RoleTargetSummary.Contains(secondDevice.Name), "Wrong replacement target");
            targetPicker.IsDropDownOpen = true; await Task.Delay(1300);
            Require(targetPicker.IsDropDownOpen && ReferenceEquals(targetPicker.SelectedItem, secondDevice) && resets == 0, "Polling interrupted target picker");
            targetPicker.IsDropDownOpen = false;
            a.DeviceSettings.SelectedRoleToInherit = a.DeviceSettings.RoleChoices.Single(c => c.Id == primaryRole.Id);
            await Execute(a, a.DeviceSettings.InheritRoleCommand);
            Require(a.DeviceControl.Roles.Single(r => r.Id == primaryRole.Id).DeviceId == secondDevice.Id, "Wrong inherited target");
            targetPicker.SetCurrentValue(ComboBox.SelectedItemProperty, firstDevice);
            a.DeviceSettings.SelectedRoleToInherit = a.DeviceSettings.RoleChoices.Single(c => c.Id == primaryRole.Id);
            await Execute(a, a.DeviceSettings.InheritRoleCommand);
            Require(a.DeviceControl.Roles.Single(r => r.Id == primaryRole.Id).DeviceId == firstDevice.Id, "Role recovery failed");
            adminTabs.SelectedIndex = 0; first.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(ReferenceEquals(deviceGrid.SelectedItem, firstDevice), "Tab change lost selected device");
            await Execute(a, a.ReleaseCommand);
            Require(!assignButton.IsEnabled && a.DeviceSettings.RoleAssignmentHint.Contains("사용권"), "Assignment remained enabled after use ended");
            await Execute(a, a.AcquireCommand);
            a.AccountManagement.NewAccountName = "operator"; a.AccountManagement.ReadNewPassword = () => host.Password;
            await Execute(a, a.AccountManagement.CreateAccountCommand); Require(a.AccountManagement.Accounts.Count == 2, a.Message);
            second = new MainWindow(false) { Title = "IntegratedContro · 로컬 WPF 검증 B" };
            var b = (MainViewModel)second.DataContext;
            b.DeviceViewIndex = 1;
            b.Endpoint = host.Endpoint; b.Fingerprint = host.Fingerprint; b.LoginName = "operator"; b.ReadLoginPassword = () => host.Password;
            second.DataContext = null; second.DataContext = b; second.Show();
            await Execute(b, b.LoginCommand); Require(b.IsLoggedIn && !b.CanControl, b.Message);
            a.DeviceControl.SelectedRole = a.DeviceControl.Roles.Single(r => r.Id == primaryRole.Id); a.DeviceControl.SelectedCapability = a.DeviceControl.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            a.DeviceControl.CommandValue = 26; a.DeviceControl.DelayMs = 1200;
            await Execute(a, a.DeviceControl.SubmitCommand);
            Require(a.JobManagement.Jobs.Count == 1, a.Message);
            Require(a.JobManagement.Jobs.Single().Job.Snapshot.Steps[0].Target!.Id == firstDevice.Id
                && a.JobManagement.Jobs.Single().Job.Snapshot.Steps[0].Target!.PcId == firstDevice.Config.PcId, "First command targeted a different device/PC");
            a.DeviceControl.CommandValue = 90; a.DeviceControl.DelayMs = 60000;
            await Execute(a, a.DeviceControl.SubmitCommand); Require(a.JobManagement.Jobs.Count == 2, a.Message);
            var longJobId = a.JobManagement.Jobs.Single(j => j.Job.Snapshot.Steps[0].Value == 90).Id;
            await Execute(a, a.ReleaseCommand);
            await Execute(b, b.RefreshCommand);
            await Execute(b, b.AcquireCommand); Require(b.CanControl, b.Message);
            b.DeviceSettings.SelectedDevice = b.DeviceSettings.Devices[0]; b.DeviceSettings.RoleName = "forbidden";
            Require(!b.DeviceSettings.SaveRoleCommand.CanExecute(null), "Operator could change role settings");
            b.JobManagement.SelectedJob = b.JobManagement.Jobs.Single(j => j.Id == longJobId);
            await Execute(b, b.JobManagement.CancelCommand);
            Require(b.JobManagement.Jobs.Single(j => j.Id == longJobId).Job.Status == JobStatus.Cancelled, b.Message);
            await Wait(() => b.JobManagement.Jobs.Any(j => j.Job.Status == JobStatus.Completed));
            Require(b.JobManagement.Jobs.All(j => j.PreviousSession), "Previous-session work label missing");
            // Polling must retain the selected role, capability and editable value.
            b.DeviceControl.SelectedRole = b.DeviceControl.Roles.Single(r => r.Id == primaryRole.Id); b.DeviceControl.SelectedCapability = b.DeviceControl.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            b.DeviceControl.CommandValue = 44; await Task.Delay(1300);
            Require(b.DeviceControl.SelectedCapability?.Operation == DeviceOperation.Brightness && b.DeviceControl.CommandValue == 44, "Refresh changed the user's chosen operation/value");
            b.DeviceSettings.SelectedDevice = b.DeviceSettings.Devices[0];
            var tabs = Find<TabControl>(second) ?? throw new InvalidOperationException("TabControl missing");
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index; second.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(second, Path.Combine(output, $"tab-{index + 1}.png"));
            }
            // Construct and save a sequential definition through the actual admin ViewModel.
            await Execute(b, b.ReleaseCommand); await Execute(a, a.RefreshCommand); await Execute(a, a.AcquireCommand);
            SetScenarioValue(a, a.DeviceControl.Roles.Single(r => r.Id == primaryRole.Id).Id, DeviceOperation.Power, 1);
            a.ScenarioEditor.DelayMs = 10000; a.ScenarioEditor.ScenarioName = "검증용 순차 시나리오";
            await Execute(a, a.ScenarioEditor.AddStepCommand);
            await Execute(a, a.ScenarioEditor.SaveScenarioCommand); Require(a.ScenarioEditor.Scenarios.Count == 1, a.Message);
            a.ScenarioEditor.SelectedScenario = a.ScenarioEditor.Scenarios[0]; await Execute(a, a.ScenarioEditor.RunScenarioCommand);
            a.JobManagement.SelectedJob = a.JobManagement.Jobs.Single(j => j.Job.Kind == JobKind.Scenario);
            a.JobManagement.ConfirmManualSwitch = _ => false; await Execute(a, a.JobManagement.ManualSwitchCommand);
            Require(a.JobManagement.Jobs.Single(j => j.Job.Kind == JobKind.Scenario).Job.Active, "Declining switch stopped scenario");
            a.JobManagement.ConfirmManualSwitch = _ => true; await Execute(a, a.JobManagement.ManualSwitchCommand);
            Require(a.JobManagement.Jobs.Single(j => j.Job.Kind == JobKind.Scenario).Job.Status == JobStatus.Cancelled, a.Message);
            a.DeviceSettings.SelectedDevice = a.DeviceSettings.Devices[0]; await Execute(a, a.DeviceSettings.ReconcileCommand);
            a.DeviceControl.SelectedRole = a.DeviceControl.Roles.Single(r => r.Id == primaryRole.Id); a.DeviceControl.SelectedCapability = a.DeviceControl.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            a.DeviceControl.CommandValue = 58; a.DeviceControl.DelayMs = 1500; await Execute(a, a.DeviceControl.SubmitCommand);
            var unattended = a.JobManagement.Jobs.Single(j => j.Job.Snapshot.Steps[0].Value == 58).Id;
            // Both real WPF windows close through production OnClosing -> Logout. Host remains separate.
            first.Close(); second.Close();
            await Wait(() => !first.IsVisible && !second.IsVisible);
            Require(!host.Process!.HasExited, "Closing WPF stopped host");
            var (observer, _) = await host.Login();
            await HostProcess.Until(observer, s => s.Jobs.Single(j => j.Id == unattended).Status == JobStatus.Completed);
            listener.Flush(); var trace = bindingLog.ToString();
            await File.WriteAllTextAsync(Path.Combine(output, "binding.log"), trace);
            Require(string.IsNullOrWhiteSpace(trace), "WPF binding warnings: " + trace);
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"),
                "PASS: two local WPF windows / real ViewModels / separate HTTPS host; login, device/role/operator registration, exclusive control, handoff, preserved/cancelled previous work, selected-value stability, actual role form bindings, stable target selection/dropdown during polling and tab changes, same-name device/location reassignment, role permission gating, scenario save/run, declined/accepted manual switch, state reconciliation, all windows closed with host work continuing. All available tabs rendered. No WPF binding warnings. Not native click automation; not two physical PCs.");
            await ProbeProductionExecutables(host.Root, output);
            Console.WriteLine($"WPF smoke PASS. Screenshots and evidence: {output}");
        }
        finally
        {
            if (first is { IsVisible: true, DataContext: MainViewModel avm }) { await avm.CloseAsync(); first.Close(); }
            if (second is { IsVisible: true, DataContext: MainViewModel bvm }) { await bvm.CloseAsync(); second.Close(); }
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
    private static async Task RunLighting()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
        var window = new MainWindow(false) { Title = "IntegratedContro · 조명 카드 검증" };
        var vm = (MainViewModel)window.DataContext;
        vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint; vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
        window.DataContext = null; window.DataContext = vm; window.Show();
        using var bindingLog = new StringWriter();
        using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            var names = new[] { "입구 조명", "테이블 조명", "복도 조명", "벽면 조명" };
            for (var i = 0; i < names.Length; i++)
            {
                await Execute(vm, vm.DeviceSettings.NewDeviceCommand);
                vm.DeviceSettings.DeviceName = names[i]; vm.DeviceSettings.ConnectionId = $"virtual.light.{i}";
                vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == (i % 2 == 0 ? "virtual-light-basic" : "virtual-light"));
                                await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
                var d = vm.DeviceSettings.Devices.Single(x => x.Id.ToString() == vm.DeviceSettings.DeviceIdText);
                vm.DeviceSettings.SelectedDevice = d; vm.DeviceSettings.RoleName = $"lighting.{i}";
                await Execute(vm, vm.DeviceSettings.SaveRoleCommand);
                await Execute(vm, vm.DeviceSettings.LoadDiagnosticsCommand); vm.DeviceSettings.DiagnosticLatencyText = "600"; await Execute(vm, vm.DeviceSettings.SaveDiagnosticsCommand);
                var card = vm.Lighting.Lights.Single(c => c.Id == d.Id);
                Require(!card.PowerCommand.CanExecute(null), "Unobserved light was presented as ready to toggle");
                await Execute(vm, card.ReadCommand);
                Require(card.Power == 0 && card.StateText.Contains("OFF"), "Initial virtual OFF state missing");
            }
            await Execute(vm, vm.DeviceSettings.NewDeviceCommand); vm.DeviceSettings.DeviceName = "구분 검증 프로젝터";
            vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-projector"); await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            Require(vm.Lighting.Lights.Count == 4 && vm.DeviceSettings.Devices.Count == 5, "Non-light device appeared in the lighting group");
            vm.DeviceViewIndex = 0; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var light = vm.Lighting.Lights[0];
            Button PowerButton(LightCard c) => FindAll<Button>(window).Single(b => b.Name == "LightPowerButton" && ReferenceEquals(b.DataContext, c));
            var button = PowerButton(light);
            await Click(vm, button);
            Require(!light.PowerCommand.CanExecute(null), "Repeated toggle enabled while its job is pending");
            await Wait(() => light.Power == 1 && light.PowerCommand.CanExecute(null));
            Require(vm.JobManagement.Jobs.Count == 1 && vm.JobManagement.Jobs.Single().Job.Snapshot.Steps[0].Target!.Id == light.Id, "Card click targeted another light");
            await Click(vm, button);
            await Wait(() => light.Power == 0 && light.PowerCommand.CanExecute(null));
            Require(vm.JobManagement.Jobs.Count == 2 && vm.JobManagement.Jobs.All(j => j.Job.Status == JobStatus.Completed), "ON then OFF did not complete");
            await Click(vm, button); await Wait(() => light.IsOn && light.PowerCommand.CanExecute(null));
            // Editing is a draft until save; cancellation restores the shared order.
            var original = vm.Lighting.Lights.Select(c => c.Id).ToArray();
            await Execute(vm, vm.Lighting.EditLightOrderCommand);
            Require(!light.PowerCommand.CanExecute(null), "Order editing allowed a power click");
            await Execute(vm, light.LaterCommand);
            await Execute(vm, vm.Lighting.CancelLightOrderCommand);
            Require(vm.Lighting.Lights.Select(c => c.Id).SequenceEqual(original), "Cancel did not restore order");
            await Execute(vm, vm.Lighting.EditLightOrderCommand); await Execute(vm, light.LaterCommand);
            var reordered = vm.Lighting.Lights.Select(c => c.Id).ToArray();
            window.UpdateLayout(); Capture(window, Path.Combine(output, "lighting-order.png"));
            await Execute(vm, vm.Lighting.SaveLightOrderCommand);
            var (observer, _) = await host.Login();
            var observed = await HostProcess.Until(observer, s => s.LightLayout.DeviceIds.SequenceEqual(reordered));
            Require(observed.LightLayout.Version == 1, "Saved order not visible to the other HTTPS session");
            await ExerciseGrouping(window, vm, host, output);
            await ExerciseBatch(window, vm, output);
            reordered = vm.Lighting.Lights.Select(c => c.Id).ToArray();
            await Execute(vm, vm.ReleaseCommand);
            Require(vm.Lighting.Lights.All(c => !c.PowerCommand.CanExecute(null)), "Read-only cards could control");
            await Execute(vm, vm.AcquireCommand);
            // Reserved scenario: no implicit cancellation or queued manual power command.
            SetScenarioValue(vm, vm.DeviceControl.Roles.First(r => r.DeviceId == light.Id && !r.IsDefault).Id, DeviceOperation.Power, 0);
            vm.ScenarioEditor.DelayMs = 60000; vm.ScenarioEditor.ScenarioName = "조명 예약 검증";
            await Execute(vm, vm.ScenarioEditor.AddStepCommand); await Execute(vm, vm.ScenarioEditor.SaveScenarioCommand);
            vm.ScenarioEditor.SelectedScenario = vm.ScenarioEditor.Scenarios.Single(); await Execute(vm, vm.ScenarioEditor.RunScenarioCommand);
            Require(!light.PowerCommand.CanExecute(null) && light.Hint.Contains("시나리오 예약"), "Reserved light was clickable");
            vm.JobManagement.SelectedJob = vm.JobManagement.Jobs.Single(j => j.Job.Kind == JobKind.Scenario);
            vm.JobManagement.ConfirmManualSwitch = _ => true; await Execute(vm, vm.JobManagement.ManualSwitchCommand);
            Require(light.NeedsCheck && !light.PowerCommand.CanExecute(null), "Uncertain light looked ready after manual switch");
            await Execute(vm, light.ReadCommand);
            Require(light.PowerCommand.CanExecute(null), "State reconciliation did not release the light");
            // Keep one unknown card to render a distinct state without inventing OFF.
            var unknown = vm.Lighting.Lights.Last();
            vm.DeviceSettings.SelectedDevice = vm.DeviceSettings.Devices.Single(d => d.Id == unknown.Id);
            await Execute(vm, vm.DeviceSettings.LoadDiagnosticsCommand); vm.DeviceSettings.DiagnosticFault = VirtualFault.Disconnected;
            await Execute(vm, vm.DeviceSettings.SaveDiagnosticsCommand);
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var countBeforeBlocked = vm.JobManagement.Jobs.Count;
            await Execute(vm, vm.Lighting.AllLightsOnCommand);
            Require(vm.JobManagement.Jobs.Count == countBeforeBlocked && vm.Message.Contains("일괄 접수하지 않았습니다"), "Unobserved target allowed partial bulk acceptance");
            Capture(window, Path.Combine(output, "lighting-cards.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, "lighting-compact.png"));
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.Lighting.Lights.Select(c => c.Id).SequenceEqual(reordered), "Reconnecting lost shared order");
            await Execute(vm, vm.LogoutCommand); // Closing without a session must also be safe.
            listener.Flush();
            await File.WriteAllTextAsync(Path.Combine(output, "lighting-binding.log"), bindingLog.ToString());
            Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Lighting binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "lighting-result.txt"),
                "PASS: real WPF card button invocation; light-only grouping, named cards, unknown/OFF/ON, ON then OFF on exact target, pending/repeated click block, order edit/cancel/save, second HTTPS session and reconnect order, read-only block, scenario reservation/manual switch/reconcile; full and compact rendering. No binding warnings. Local code-driven WPF; no physical touch device or second PC.");
            Console.WriteLine("Lighting WPF smoke PASS.");
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible);
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
    private static async Task ExerciseBatch(MainWindow window, MainViewModel vm, string output)
    {
        var board = Find<LightingView>(window)!;
        async Task WaitBatch(int value, IEnumerable<LightCard> cards)
        {
            var targets = cards.ToArray();
            await Wait(() => targets.All(c => c.Power == value && c.PowerCommand.CanExecute(null)) &&
                vm.JobManagement.Jobs.All(j => !j.Job.Active));
        }
        await Click(vm, (Button)board.FindName("AllLightsOn"));
        await WaitBatch(1, vm.Lighting.Lights);
        Require(vm.JobManagement.Jobs.First().Job.IsLightBatch && vm.JobManagement.Jobs.First().Job.Snapshot.Steps.Length == 4, "All ON omitted a light");
        await Click(vm, (Button)board.FindName("AllLightsOff")); await WaitBatch(0, vm.Lighting.Lights);
        var group = vm.Lighting.LightGroups.Single(g => !g.IsDefault);
        Button GroupButton(string name) => FindAll<Button>(board).Single(b => b.Name == name && ReferenceEquals(b.DataContext, group));
        await Click(vm, GroupButton("GroupOn")); await WaitBatch(1, group.Cards);
        Require(vm.Lighting.Lights.Except(group.Cards).All(c => c.Power == 0), "Group command changed an outside light");
        Require(vm.JobManagement.Jobs.First().Job.Snapshot.Steps.Select(s => s.Target!.Id).SequenceEqual(group.Cards.Select(c => c.Id)), "Group snapshot mismatch");
        await Click(vm, GroupButton("GroupOff")); await WaitBatch(0, group.Cards);
        await Execute(vm, vm.Lighting.EditLightOrderCommand);
        Require(!vm.Lighting.AllLightsOnCommand.CanExecute(null) && !group.OnCommand.CanExecute(null), "Layout edit allowed bulk power");
        await Execute(vm, vm.Lighting.CancelLightOrderCommand);
        await Execute(vm, vm.ReleaseCommand);
        Require(!vm.Lighting.AllLightsOffCommand.CanExecute(null) && !group.OffCommand.CanExecute(null), "Read-only mode allowed bulk power");
        await Execute(vm, vm.AcquireCommand);
        var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "복구 · 진단");
        window.UpdateLayout();
        Require(vm.Recovery.Audit.Any(a => a.Event == "조명 일괄 명령 접수" && a.Summary.Contains("단계")), "Readable audit message missing");
        vm.Recovery.SelectedAudit = vm.Recovery.Audit.First(a => a.Entry.Action == "DispatchResult");
        Require(vm.Recovery.AuditDetails.Contains("job=") && vm.Recovery.SelectedAudit.Summary.Contains("가상 실행 완료"), "Audit lost raw detail or result label");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var auditGrid = (DataGrid)window.FindName("AuditGrid");
        Require(auditGrid.Columns.Take(3).All(c => c.ActualWidth >= 100) && auditGrid.Columns.Last().ActualWidth >= 360,
            "Audit columns collapsed before rendering");
        Capture(window, Path.Combine(output, "readable-audit.png"));
        tabs.SelectedIndex = 0; window.UpdateLayout();
        Capture(window, Path.Combine(output, "lighting-bulk.png"));
        await File.WriteAllTextAsync(Path.Combine(output, "lighting-bulk-result.txt"),
            "PASS: actual WPF all/group ON/OFF button invocation against separate HTTPS host; absolute requested states, frozen exact target set, outside group unchanged, edit/read-only blocking, readable audit and preserved raw detail. No physical devices.");
    }
    private static async Task ExerciseGrouping(MainWindow window, MainViewModel vm, HostProcess host, string output)
    {
        var board = Find<LightingView>(window)!;
        var originalHeight = window.Height; window.Height = 1200; window.UpdateLayout();
        var original = vm.Lighting.Lights.Select(c => c.Id).ToArray();
        var jobCount = vm.JobManagement.Jobs.Count;
        await Execute(vm, vm.Lighting.EditLightOrderCommand);
        Require(vm.Lighting.Lights.All(c => c.StateText is "ON" or "OFF") && vm.Lighting.Lights.All(c => c.Hint == ""), "Compact card retained Korean state/editor text");
        var groupName = (TextBox)board.FindName("NewGroupName");
        groupName.SetCurrentValue(TextBox.TextProperty, "전시 구역");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Click(vm, (Button)board.FindName("AddGroup"));
        var group = vm.Lighting.LightGroups.Single(g => !g.IsDefault);
        Require(group.Cards.Count == 0, "New group should be empty");
        FrameworkElement Tile(Guid id) => FindAll<FrameworkElement>(board).Single(e => e.Name == "CardContainer" && e.DataContext is LightCard c && c.Id == id);
        FrameworkElement Zone(Guid id) => FindAll<FrameworkElement>(board).Single(e => e.Name == "GroupDropZone" && e.DataContext is LightGroupRow g && g.Id == id);
        Point Position(FrameworkElement e, double x, double y) => e.TranslatePoint(new Point(x, y), board);
        void Drag(Guid id, FrameworkElement target, double x, double y, int pointerId)
        {
            window.UpdateLayout();
            var tile = Tile(id); var start = Position(tile, 30, 35); var end = Position(target, x, y);
            Require(board.BeginCardDrag(id, start, pointerId), "Pointer drag did not begin");
            board.MoveCardDrag(end, pointerId);
            Require(board.EndCardDrag(end, pointerId), "Pointer drop did not reorder");
            window.UpdateLayout();
        }
        window.UpdateLayout();
        var first = vm.Lighting.Lights.Single(c => c.Id == original[0]);
        // Click-sized movement in edit mode does not reorder or send power.
        var point = Position(Tile(first.Id), 25, 30);
        var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        ((UIElement)board.InputHitTest(point)).RaiseEvent(mouseDown);
        Require(mouseDown.Handled, "Routed mouse down was not intercepted for editing");
        // Synthetic mouse events have no OS button press; capture may be released immediately.
        // Real touch capture/release is tested with a synthetic WPF TouchDevice below.
        board.CancelCardDrag();
        using (var touch = new RecordedTouch(board, 901, point))
        {
            var touchDown = new TouchEventArgs(touch, Environment.TickCount) { RoutedEvent = UIElement.PreviewTouchDownEvent };
            ((UIElement)board.InputHitTest(point)).RaiseEvent(touchDown);
            Require(touchDown.Handled && ReferenceEquals(touch.Captured, board), "Routed touch was not captured");
            var touchUp = new TouchEventArgs(touch, Environment.TickCount) { RoutedEvent = UIElement.PreviewTouchUpEvent };
            board.RaiseEvent(touchUp);
            Require(touchUp.Handled && touch.Captured is null, "Routed touch release did not suppress the click/release capture");
        }
        Require(vm.JobManagement.Jobs.Count == jobCount, "Edit-mode pointer click sent power");
        Require(board.BeginCardDrag(first.Id, point, -1), "Mouse down rejected");
        Require(!board.EndCardDrag(point + new Vector(2, 2), -1), "Click-sized move reordered");
        Require(vm.Lighting.Lights.Select(c => c.Id).SequenceEqual(original), "Short gesture changed order");
        // Mouse path into an empty group.
        var zone = Zone(group.Id);
        Drag(first.Id, zone, 30, zone.ActualHeight - 20, -1);
        Require(group.Cards.Single().Id == first.Id, "Empty group did not receive light");
        await Task.Delay(1200);
        Require(group.Cards.Single().Id == first.Id, "Polling overwrote unsaved membership");
        // Touch pointer path inserts before an existing card and ignores another finger.
        var second = vm.Lighting.Lights.Single(c => c.Id == original[1]);
        window.UpdateLayout(); var start = Position(Tile(second.Id), 25, 30);
        var end = Position(Tile(first.Id), 4, 50);
        Require(board.BeginCardDrag(second.Id, start, 77), "Touch pointer down rejected");
        Require(!board.EndCardDrag(end, 78), "Unrelated touch ended the gesture");
        board.MoveCardDrag(end, 77); Require(board.EndCardDrag(end, 77), "Touch pointer drop rejected");
        Require(group.Cards.Select(c => c.Id).SequenceEqual(new[] { second.Id, first.Id }), "Touch insertion position incorrect");
        window.UpdateLayout();
        // Outside drop, explicit cancellation, and a stale pointer after lost ownership do nothing.
        var snapshot = vm.Lighting.Lights.Select(c => c.Id).ToArray();
        start = Position(Tile(first.Id), 25, 30);
        Require(board.BeginCardDrag(first.Id, start, -1), "Outside-drop start failed");
        Require(!board.EndCardDrag(new Point(-50, -50), -1), "Outside drop was accepted");
        Require(board.BeginCardDrag(first.Id, start, 88), "Cancel start failed");
        board.MoveCardDrag(end + new Vector(30, 0), 88); board.CancelCardDrag();
        Require(!board.EndCardDrag(end, 88), "Cancelled pointer committed");
        Require(vm.Lighting.Lights.Select(c => c.Id).SequenceEqual(snapshot), "Cancelled gesture changed layout");
        group.Name = "무대 조명";
        window.UpdateLayout();
        Require(Math.Abs(Position(Tile(first.Id), 0, 0).Y - Position(Tile(second.Id), 0, 0).Y) < 1, "Two compact cards did not fit in one group row");
        Capture(window, Path.Combine(output, "lighting-groups-edit.png"));
        await Execute(vm, vm.Lighting.CancelLightOrderCommand);
        Require(vm.Lighting.LightGroups.All(g => g.IsDefault) && vm.Lighting.Lights.Select(c => c.Id).SequenceEqual(original), "Cancel did not discard groups and order");
        // Repeat and save; deleting the draft group returns members without deleting a device.
        await Execute(vm, vm.Lighting.EditLightOrderCommand);
        vm.Lighting.NewLightGroupName = "무대 조명"; await Execute(vm, vm.Lighting.AddLightGroupCommand);
        group = vm.Lighting.LightGroups.Single(g => !g.IsDefault); window.UpdateLayout();
        zone = Zone(group.Id); Drag(first.Id, zone, 30, zone.ActualHeight - 20, -1);
        await Execute(vm, group.RemoveCommand);
        Require(vm.Lighting.LightGroups.Single().Cards.Count == 4 && vm.DeviceSettings.Devices.Count == 5, "Group deletion deleted devices");
        vm.Lighting.NewLightGroupName = "무대 조명"; await Execute(vm, vm.Lighting.AddLightGroupCommand);
        group = vm.Lighting.LightGroups.Single(g => !g.IsDefault); window.UpdateLayout();
        zone = Zone(group.Id); Drag(first.Id, zone, 30, zone.ActualHeight - 20, -1);
        Drag(second.Id, Tile(first.Id), 4, 50, 99);
        Require(vm.JobManagement.Jobs.Count == jobCount, "A drag or group edit sent a power job");
        await Execute(vm, vm.Lighting.SaveLightOrderCommand);
        var (observer, _) = await host.Login();
        var state = await HostProcess.Until(observer, s => s.LightLayout.Groups.Length == 1);
        Require(state.LightLayout.Groups[0].Name == "무대 조명" &&
            state.LightLayout.Groups[0].DeviceIds.SequenceEqual(group.Cards.Select(c => c.Id)), "Group did not persist to HTTPS state");
        await Execute(vm, vm.Lighting.EditLightOrderCommand);
        window.UpdateLayout(); start = Position(Tile(first.Id), 25, 30);
        Require(board.BeginCardDrag(first.Id, start, -1), "Lease-loss start failed");
        await Execute(vm, vm.ReleaseCommand);
        Require(!board.EndCardDrag(end, -1), "Pointer committed after use ended");
        await Execute(vm, vm.Lighting.CancelLightOrderCommand); await Execute(vm, vm.AcquireCommand);
        Require(!board.BeginCardDrag(first.Id, start, -1), "Normal power mode started a drag");
        window.Height = originalHeight; window.UpdateLayout();
        Capture(window, Path.Combine(output, "lighting-groups.png"));
        await File.WriteAllTextAsync(Path.Combine(output, "lighting-group-drag-result.txt"),
            "PASS: compact ON/OFF cards; group add/rename/delete/cancel/save and second HTTPS session; routed mouse interception and synthetic WPF touch capture/release; shared production pointer pipeline with mouse and touch IDs; threshold, before-card insertion, empty group, outside/cancelled/wrong-pointer drop, poll stability and ownership loss; no extra power jobs. WPF hit-testing/code-driven input, not physical mouse/touch hardware.");
    }
    private sealed class RecordedTouch : TouchDevice, IDisposable
    {
        private readonly UIElement _root;
        private readonly Point _point;
        public RecordedTouch(UIElement root, int id, Point point) : base(id)
        {
            _root = root; _point = point;
            SetActiveSource(PresentationSource.FromVisual(root)); Activate();
        }
        public override TouchPoint GetTouchPoint(IInputElement relativeTo)
        {
            var point = relativeTo is UIElement element ? _root.TranslatePoint(_point, element) : _point;
            return new TouchPoint(this, point, new Rect(point, new Size(1, 1)), TouchAction.Move);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => [GetTouchPoint(relativeTo)];
        public void Dispose() { Capture(null); Deactivate(); }
    }
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task ProbeProductionExecutables(string root, string output)
    {
        var exe = Path.Combine(root, "src", "IntegratedContro.App", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0-windows", "win-x64", "IntegratedContro.App.exe");
        var processes = new List<Process>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--profile-dir"); start.ArgumentList.Add(Path.Combine(output, $"exe-profile-{i}"));
                var process = Process.Start(start) ?? throw new InvalidOperationException("WPF EXE did not start");
                processes.Add(process);
            }
            await Wait(() => processes.All(p => { p.Refresh(); return !p.HasExited && p.MainWindowHandle != IntPtr.Zero; }));
            Require(processes.All(p => p.MainWindowTitle.Contains("IntegratedContro")), "Unexpected production WPF window");
            await File.WriteAllTextAsync(Path.Combine(output, "exe-startup.txt"), "PASS: two distinct production WPF EXE processes exposed their main windows. Login and workflows are covered by the separate real-window/ViewModel harness.");
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                process.Dispose();
            }
        }
    }
    private static async Task Click(MainViewModel vm, Button button)
    {
        await Wait(() => !vm.IsBusy);
        Require(button.IsEnabled, "WPF button is disabled");
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Wait(() => !vm.IsBusy);
    }
    private static async Task Execute(MainViewModel vm, AsyncCommand command)
    {
        await Wait(() => !vm.IsBusy);
        Require(command.CanExecute(null), $"Command disabled: {vm.Message}");
        command.Execute(null); await Wait(() => !vm.IsBusy);
    }
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF condition timeout");
            await Task.Delay(50);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
