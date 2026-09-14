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
        var app = new System.Windows.Application();
        app.Resources = new ResourceDictionary { Source = new Uri("/IntegratedContro.App;component/Theme.xaml", UriKind.Relative) }; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args.Contains("--storage-only")) await RunStorage();
                else if (args.Contains("--handover-only")) await RunHandover();
                else
                {
                    if (!args.Contains("--hiperwall-only")) { await RunLogin(); await Run(); await RunLighting(); }
                    await RunHiperwall(); await RunHiperwallEditing(); await RunHandover(); await RunStorage();
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
            ((TextBox)dialog.FindName("HostEndpoint")).SetCurrentValue(TextBox.TextProperty, host.Endpoint);
            ((TextBox)dialog.FindName("CertificateFingerprint")).SetCurrentValue(TextBox.TextProperty, host.Fingerprint);
            ((TextBox)dialog.FindName("LoginNameInput")).SetCurrentValue(TextBox.TextProperty, "admin");
            var password = (PasswordBox)dialog.FindName("LoginPassword");
            password.Password = "invalid-test-password";
            await Click(vm, (Button)dialog.FindName("ConnectButton"));
            Require(!vm.IsLoggedIn && dialog.IsVisible && password.Password == "", "Failed login closed popup or retained password");
            Capture(dialog, Path.Combine(output, "login-popup.png")); // No password is present in the evidence.
            password.Password = host.Password;
            await Click(vm, (Button)dialog.FindName("ConnectButton"));
            await Wait(() => window.LoginDialog is null);
            Require(vm.IsLoggedIn && ((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Visible, "Admin login did not expose admin tab");
            Require(!(await File.ReadAllTextAsync(ClientPreferences.ProfilePath)).Contains(host.Password), "Password was persisted");
            await Execute(vm, vm.AcquireCommand);
            vm.NewAccountName = "operator"; vm.NewAccountRole = AccountRole.Operator; vm.ReadNewPassword = () => host.Password;
            await Execute(vm, vm.CreateAccountCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("AdminTab");
            await Click(vm, (Button)window.FindName("HeaderLogout"));
            await Wait(() => window.LoginDialog?.IsVisible == true);
            Require(((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Collapsed &&
                ((TabControl)window.FindName("MainTabs")).SelectedIndex == 0, "Logout retained admin page");
            dialog = window.LoginDialog!;
            ((TextBox)dialog.FindName("LoginNameInput")).SetCurrentValue(TextBox.TextProperty, "operator");
            ((PasswordBox)dialog.FindName("LoginPassword")).Password = host.Password;
            await Click(vm, (Button)dialog.FindName("ConnectButton")); await Wait(() => window.LoginDialog is null);
            Require(vm.IsLoggedIn && !vm.IsAdmin && ((TabItem)window.FindName("AdminTab")).Visibility == Visibility.Collapsed,
                "Operator could see admin settings");
            Require(!vm.SaveDeviceCommand.CanExecute(null), "Hidden admin form retained write access");
            Capture(window, Path.Combine(output, "operator-home.png"));
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Login binding errors: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "login-result.txt"),
                "PASS: startup modal fields and actual WPF bindings; failed login retains dialog and clears password; successful login closes dialog; header logout reopens popup; operator cannot see admin tab; selected admin tab removed after logout; password not saved. Local code-driven WPF.");
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
            a.DeviceName = "검증용 가상 조명"; a.ConnectionId = "virtual.local";
            a.SelectedModel = a.Models.Single(m => m.Id == "virtual-light");
            await Execute(a, a.SaveDeviceCommand); Require(a.Devices.Count == 1, a.Message);
            var deviceGrid = (DataGrid)first.FindName("DeviceGrid");
            var roleText = (TextBox)first.FindName("QuickRoleName");
            var assignButton = (Button)first.FindName("QuickAssignRole");
            var controlRole = (ComboBox)first.FindName("ControlRolePicker");
            var firstDevice = a.Devices[0];
            Require(!assignButton.IsEnabled, "Role assignment enabled without a selected device/name");
            deviceGrid.SetCurrentValue(DataGrid.SelectedItemProperty, firstDevice);
            roleText.SetCurrentValue(TextBox.TextProperty, "   ");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!assignButton.IsEnabled, "Blank role ID was accepted by the UI");
            roleText.SetCurrentValue(TextBox.TextProperty, "room.light");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(a.SelectedDevice?.Id == firstDevice.Id && assignButton.IsEnabled, "Grid/text bindings did not enable assignment");
            var resets = 0;
            a.Devices.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
            await Execute(a, a.RefreshCommand);
            await Task.Delay(1300);
            Require(ReferenceEquals(firstDevice, a.SelectedDevice) && ReferenceEquals(deviceGrid.SelectedItem, firstDevice)
                && resets == 0 && a.RoleName == "room.light", "Polling reset the device selection or role draft");
            await Click(a, assignButton);
            Require(a.Roles.Count == 1 && a.Roles[0].DeviceId == firstDevice.Id, a.Message);
            Require(a.SelectedRole?.Id == "room.light" && ReferenceEquals(controlRole.SelectedItem, a.SelectedRole), "Saved role was not selected for control");
            first.UpdateLayout();
            Capture(first, Path.Combine(output, "role-assignment.png"));

            // Same display name on another explicit virtual PC must not redirect the selected target.
            await Execute(a, a.NewDeviceCommand);
            a.DeviceName = firstDevice.Name; a.PcIdText = Guid.NewGuid().ToString(); a.PcName = "가상 대상 PC B";
            await Execute(a, a.SaveDeviceCommand); Require(a.Devices.Count == 2, a.Message);
            var secondDevice = a.Devices.Single(d => d.Id != firstDevice.Id);
            var adminTabs = Find<TabControl>(first)!; adminTabs.SelectedItem = first.FindName("AdminTab"); first.UpdateLayout();
            var targetPicker = (ComboBox)first.FindName("RoleDevicePicker");
            targetPicker.SetCurrentValue(ComboBox.SelectedItemProperty, secondDevice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(a.SelectedDevice?.Id == secondDevice.Id && a.RoleTargetSummary.Contains(secondDevice.Id.ToString()), "Admin picker did not show the exact target");
            targetPicker.IsDropDownOpen = true;
            await Task.Delay(1300);
            Require(targetPicker.IsDropDownOpen && ReferenceEquals(targetPicker.SelectedItem, secondDevice)
                && resets == 0, "Polling interrupted the target picker");
            targetPicker.IsDropDownOpen = false;
            await Execute(a, a.SaveRoleCommand);
            Require(a.Roles.Single().DeviceId == secondDevice.Id, "Explicit role reassignment selected the wrong PC/device");
            targetPicker.SetCurrentValue(ComboBox.SelectedItemProperty, firstDevice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Execute(a, a.SaveRoleCommand);
            Require(a.Roles.Single().DeviceId == firstDevice.Id, "Role reassignment back to the first device failed");
            adminTabs.SelectedIndex = 0; first.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(ReferenceEquals(deviceGrid.SelectedItem, firstDevice), "Tab change lost the selected device");
            await Execute(a, a.ReleaseCommand);
            Require(!assignButton.IsEnabled && a.RoleAssignmentHint.Contains("사용권"), "Assignment remained enabled after use ended");
            await Execute(a, a.AcquireCommand);
            a.NewAccountName = "operator"; a.ReadNewPassword = () => host.Password;
            await Execute(a, a.CreateAccountCommand); Require(a.Accounts.Count == 2, a.Message);
            second = new MainWindow(false) { Title = "IntegratedContro · 로컬 WPF 검증 B" };
            var b = (MainViewModel)second.DataContext;
            b.DeviceViewIndex = 1;
            b.Endpoint = host.Endpoint; b.Fingerprint = host.Fingerprint; b.LoginName = "operator"; b.ReadLoginPassword = () => host.Password;
            second.DataContext = null; second.DataContext = b; second.Show();
            await Execute(b, b.LoginCommand); Require(b.IsLoggedIn && !b.CanControl, b.Message);
            a.SelectedRole = a.Roles[0]; a.SelectedCapability = a.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            a.CommandValue = 26; a.DelayMs = 1200;
            await Execute(a, a.SubmitCommand);
            Require(a.Jobs.Count == 1, a.Message);
            Require(a.Jobs.Single().Job.Snapshot.Steps[0].Target.Id == firstDevice.Id
                && a.Jobs.Single().Job.Snapshot.Steps[0].Target.PcId == firstDevice.Config.PcId, "First command targeted a different device/PC");
            a.CommandValue = 90; a.DelayMs = 60000;
            await Execute(a, a.SubmitCommand); Require(a.Jobs.Count == 2, a.Message);
            var longJobId = a.Jobs.Single(j => j.Job.Snapshot.Steps[0].Value == 90).Id;
            await Execute(a, a.ReleaseCommand);
            await Execute(b, b.RefreshCommand);
            await Execute(b, b.AcquireCommand); Require(b.CanControl, b.Message);
            b.SelectedDevice = b.Devices[0]; b.RoleName = "forbidden";
            Require(!b.SaveRoleCommand.CanExecute(null), "Operator could change role settings");
            b.SelectedJob = b.Jobs.Single(j => j.Id == longJobId);
            await Execute(b, b.CancelCommand);
            Require(b.Jobs.Single(j => j.Id == longJobId).Job.Status == JobStatus.Cancelled, b.Message);
            await Wait(() => b.Jobs.Any(j => j.Job.Status == JobStatus.Completed));
            Require(b.Jobs.All(j => j.PreviousSession), "Previous-session work label missing");
            // Polling must retain the selected role, capability and editable value.
            b.SelectedRole = b.Roles[0]; b.SelectedCapability = b.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            b.CommandValue = 44; await Task.Delay(1300);
            Require(b.SelectedCapability?.Operation == DeviceOperation.Brightness && b.CommandValue == 44, "Refresh changed the user's chosen operation/value");
            b.SelectedDevice = b.Devices[0];
            var tabs = Find<TabControl>(second) ?? throw new InvalidOperationException("TabControl missing");
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index; second.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(second, Path.Combine(output, $"tab-{index + 1}.png"));
            }
            // Construct and save a sequential definition through the actual admin ViewModel.
            await Execute(b, b.ReleaseCommand); await Execute(a, a.RefreshCommand); await Execute(a, a.AcquireCommand);
            a.SelectedRole = a.Roles[0]; a.SelectedCapability = a.Capabilities.Single(c => c.Operation == DeviceOperation.Power);
            a.CommandValue = 1; a.DelayMs = 10000; a.ScenarioName = "검증용 순차 시나리오";
            await Execute(a, a.AddStepCommand);
            await Execute(a, a.SaveScenarioCommand); Require(a.Scenarios.Count == 1, a.Message);
            a.SelectedScenario = a.Scenarios[0]; await Execute(a, a.RunScenarioCommand);
            a.SelectedJob = a.Jobs.Single(j => j.Job.Kind == JobKind.Scenario);
            a.ConfirmManualSwitch = _ => false; await Execute(a, a.ManualSwitchCommand);
            Require(a.Jobs.Single(j => j.Job.Kind == JobKind.Scenario).Job.Active, "Declining switch stopped scenario");
            a.ConfirmManualSwitch = _ => true; await Execute(a, a.ManualSwitchCommand);
            Require(a.Jobs.Single(j => j.Job.Kind == JobKind.Scenario).Job.Status == JobStatus.Cancelled, a.Message);
            a.SelectedDevice = a.Devices[0]; await Execute(a, a.ReconcileCommand);
            a.SelectedRole = a.Roles[0]; a.SelectedCapability = a.Capabilities.Single(c => c.Operation == DeviceOperation.Brightness);
            a.CommandValue = 58; a.DelayMs = 1500; await Execute(a, a.SubmitCommand);
            var unattended = a.Jobs.Single(j => j.Job.Snapshot.Steps[0].Value == 58).Id;
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
                "PASS: two local WPF windows / real ViewModels / separate HTTPS host; login, device/role/operator registration, exclusive control, handoff, preserved/cancelled previous work, selected-value stability, actual role form bindings, stable target selection/dropdown during polling and tab changes, same-name device/PC reassignment, role permission gating, scenario save/run, declined/accepted manual switch, state reconciliation, all windows closed with host work continuing. Five tabs rendered. No WPF binding warnings. Not native click automation; not two physical PCs.");
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
                await Execute(vm, vm.NewDeviceCommand);
                vm.DeviceName = names[i]; vm.ConnectionId = $"virtual.light.{i}";
                vm.SelectedModel = vm.Models.Single(m => m.Id == (i % 2 == 0 ? "virtual-light-basic" : "virtual-light"));
                vm.DeviceLatencyMs = 600;
                await Execute(vm, vm.SaveDeviceCommand);
                var d = vm.Devices.Single(x => x.Id.ToString() == vm.DeviceIdText);
                vm.SelectedDevice = d; vm.RoleName = $"lighting.{i}";
                await Execute(vm, vm.SaveRoleCommand);
                var card = vm.Lights.Single(c => c.Id == d.Id);
                Require(!card.PowerCommand.CanExecute(null), "Unobserved light was presented as ready to toggle");
                await Execute(vm, card.ReadCommand);
                Require(card.Power == 0 && card.StateText.Contains("OFF"), "Initial virtual OFF state missing");
            }
            await Execute(vm, vm.NewDeviceCommand); vm.DeviceName = "구분 검증 프로젝터";
            vm.SelectedModel = vm.Models.Single(m => m.Id == "virtual-projector"); await Execute(vm, vm.SaveDeviceCommand);
            Require(vm.Lights.Count == 4 && vm.Devices.Count == 5, "Non-light device appeared in the lighting group");
            vm.DeviceViewIndex = 0; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var light = vm.Lights[0];
            Button PowerButton(LightCard c) => FindAll<Button>(window).Single(b => b.Name == "LightPowerButton" && ReferenceEquals(b.DataContext, c));
            var button = PowerButton(light);
            await Click(vm, button);
            Require(!light.PowerCommand.CanExecute(null), "Repeated toggle enabled while its job is pending");
            await Wait(() => light.Power == 1 && light.PowerCommand.CanExecute(null));
            Require(vm.Jobs.Count == 1 && vm.Jobs.Single().Job.Snapshot.Steps[0].Target.Id == light.Id, "Card click targeted another light");
            await Click(vm, button);
            await Wait(() => light.Power == 0 && light.PowerCommand.CanExecute(null));
            Require(vm.Jobs.Count == 2 && vm.Jobs.All(j => j.Job.Status == JobStatus.Completed), "ON then OFF did not complete");
            await Click(vm, button); await Wait(() => light.IsOn && light.PowerCommand.CanExecute(null));
            // Editing is a draft until save; cancellation restores the shared order.
            var original = vm.Lights.Select(c => c.Id).ToArray();
            await Execute(vm, vm.EditLightOrderCommand);
            Require(!light.PowerCommand.CanExecute(null), "Order editing allowed a power click");
            await Execute(vm, light.LaterCommand);
            await Execute(vm, vm.CancelLightOrderCommand);
            Require(vm.Lights.Select(c => c.Id).SequenceEqual(original), "Cancel did not restore order");
            await Execute(vm, vm.EditLightOrderCommand); await Execute(vm, light.LaterCommand);
            var reordered = vm.Lights.Select(c => c.Id).ToArray();
            window.UpdateLayout(); Capture(window, Path.Combine(output, "lighting-order.png"));
            await Execute(vm, vm.SaveLightOrderCommand);
            var (observer, _) = await host.Login();
            var observed = await HostProcess.Until(observer, s => s.LightLayout.DeviceIds.SequenceEqual(reordered));
            Require(observed.LightLayout.Version == 1, "Saved order not visible to the other HTTPS session");
            await ExerciseGrouping(window, vm, host, output);
            await ExerciseBatch(window, vm, output);
            reordered = vm.Lights.Select(c => c.Id).ToArray();
            await Execute(vm, vm.ReleaseCommand);
            Require(vm.Lights.All(c => !c.PowerCommand.CanExecute(null)), "Read-only cards could control");
            await Execute(vm, vm.AcquireCommand);
            // Reserved scenario: no implicit cancellation or queued manual power command.
            vm.SelectedRole = vm.Roles.Single(r => r.DeviceId == light.Id);
            vm.SelectedCapability = vm.Capabilities.Single(c => c.Operation == DeviceOperation.Power);
            vm.CommandValue = 0; vm.DelayMs = 60000; vm.ScenarioName = "조명 예약 검증";
            await Execute(vm, vm.AddStepCommand); await Execute(vm, vm.SaveScenarioCommand);
            vm.SelectedScenario = vm.Scenarios.Single(); await Execute(vm, vm.RunScenarioCommand);
            Require(!light.PowerCommand.CanExecute(null) && light.Hint.Contains("시나리오 예약"), "Reserved light was clickable");
            vm.SelectedJob = vm.Jobs.Single(j => j.Job.Kind == JobKind.Scenario);
            vm.ConfirmManualSwitch = _ => true; await Execute(vm, vm.ManualSwitchCommand);
            Require(light.NeedsCheck && !light.PowerCommand.CanExecute(null), "Uncertain light looked ready after manual switch");
            await Execute(vm, light.ReadCommand);
            Require(light.PowerCommand.CanExecute(null), "State reconciliation did not release the light");
            // Keep one unknown card to render a distinct state without inventing OFF.
            var unknown = vm.Lights.Last();
            vm.SelectedDevice = vm.Devices.Single(d => d.Id == unknown.Id);
            await Execute(vm, vm.LoadDeviceCommand); vm.DeviceFault = VirtualFault.Disconnected;
            await Execute(vm, vm.SaveDeviceCommand);
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var countBeforeBlocked = vm.Jobs.Count;
            await Execute(vm, vm.AllLightsOnCommand);
            Require(vm.Jobs.Count == countBeforeBlocked && vm.Message.Contains("일괄 접수하지 않았습니다"), "Unobserved target allowed partial bulk acceptance");
            Capture(window, Path.Combine(output, "lighting-cards.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, "lighting-compact.png"));
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            Require(vm.Lights.Select(c => c.Id).SequenceEqual(reordered), "Reconnecting lost shared order");
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
                vm.Jobs.All(j => !j.Job.Active));
        }
        await Click(vm, (Button)board.FindName("AllLightsOn"));
        await WaitBatch(1, vm.Lights);
        Require(vm.Jobs.First().Job.IsLightBatch && vm.Jobs.First().Job.Snapshot.Steps.Length == 4, "All ON omitted a light");
        await Click(vm, (Button)board.FindName("AllLightsOff")); await WaitBatch(0, vm.Lights);
        var group = vm.LightGroups.Single(g => !g.IsDefault);
        Button GroupButton(string name) => FindAll<Button>(board).Single(b => b.Name == name && ReferenceEquals(b.DataContext, group));
        await Click(vm, GroupButton("GroupOn")); await WaitBatch(1, group.Cards);
        Require(vm.Lights.Except(group.Cards).All(c => c.Power == 0), "Group command changed an outside light");
        Require(vm.Jobs.First().Job.Snapshot.Steps.Select(s => s.Target.Id).SequenceEqual(group.Cards.Select(c => c.Id)), "Group snapshot mismatch");
        await Click(vm, GroupButton("GroupOff")); await WaitBatch(0, group.Cards);
        await Execute(vm, vm.EditLightOrderCommand);
        Require(!vm.AllLightsOnCommand.CanExecute(null) && !group.OnCommand.CanExecute(null), "Layout edit allowed bulk power");
        await Execute(vm, vm.CancelLightOrderCommand);
        await Execute(vm, vm.ReleaseCommand);
        Require(!vm.AllLightsOffCommand.CanExecute(null) && !group.OffCommand.CanExecute(null), "Read-only mode allowed bulk power");
        await Execute(vm, vm.AcquireCommand);
        var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "복구 · 진단");
        window.UpdateLayout();
        Require(vm.Audit.Any(a => a.Event == "조명 일괄 명령 접수" && a.Summary.Contains("단계")), "Readable audit message missing");
        vm.SelectedAudit = vm.Audit.First(a => a.Entry.Action == "DispatchResult");
        Require(vm.AuditDetails.Contains("job=") && vm.SelectedAudit.Summary.Contains("가상 실행 완료"), "Audit lost raw detail or result label");
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
        var original = vm.Lights.Select(c => c.Id).ToArray();
        var jobCount = vm.Jobs.Count;
        await Execute(vm, vm.EditLightOrderCommand);
        Require(vm.Lights.All(c => c.StateText is "ON" or "OFF") && vm.Lights.All(c => c.Hint == ""), "Compact card retained Korean state/editor text");
        var groupName = (TextBox)board.FindName("NewGroupName");
        groupName.SetCurrentValue(TextBox.TextProperty, "전시 구역");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Click(vm, (Button)board.FindName("AddGroup"));
        var group = vm.LightGroups.Single(g => !g.IsDefault);
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
        var first = vm.Lights.Single(c => c.Id == original[0]);
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
        Require(vm.Jobs.Count == jobCount, "Edit-mode pointer click sent power");
        Require(board.BeginCardDrag(first.Id, point, -1), "Mouse down rejected");
        Require(!board.EndCardDrag(point + new Vector(2, 2), -1), "Click-sized move reordered");
        Require(vm.Lights.Select(c => c.Id).SequenceEqual(original), "Short gesture changed order");
        // Mouse path into an empty group.
        var zone = Zone(group.Id);
        Drag(first.Id, zone, 30, zone.ActualHeight - 20, -1);
        Require(group.Cards.Single().Id == first.Id, "Empty group did not receive light");
        await Task.Delay(1200);
        Require(group.Cards.Single().Id == first.Id, "Polling overwrote unsaved membership");
        // Touch pointer path inserts before an existing card and ignores another finger.
        var second = vm.Lights.Single(c => c.Id == original[1]);
        window.UpdateLayout(); var start = Position(Tile(second.Id), 25, 30);
        var end = Position(Tile(first.Id), 4, 50);
        Require(board.BeginCardDrag(second.Id, start, 77), "Touch pointer down rejected");
        Require(!board.EndCardDrag(end, 78), "Unrelated touch ended the gesture");
        board.MoveCardDrag(end, 77); Require(board.EndCardDrag(end, 77), "Touch pointer drop rejected");
        Require(group.Cards.Select(c => c.Id).SequenceEqual(new[] { second.Id, first.Id }), "Touch insertion position incorrect");
        window.UpdateLayout();
        // Outside drop, explicit cancellation, and a stale pointer after lost ownership do nothing.
        var snapshot = vm.Lights.Select(c => c.Id).ToArray();
        start = Position(Tile(first.Id), 25, 30);
        Require(board.BeginCardDrag(first.Id, start, -1), "Outside-drop start failed");
        Require(!board.EndCardDrag(new Point(-50, -50), -1), "Outside drop was accepted");
        Require(board.BeginCardDrag(first.Id, start, 88), "Cancel start failed");
        board.MoveCardDrag(end + new Vector(30, 0), 88); board.CancelCardDrag();
        Require(!board.EndCardDrag(end, 88), "Cancelled pointer committed");
        Require(vm.Lights.Select(c => c.Id).SequenceEqual(snapshot), "Cancelled gesture changed layout");
        group.Name = "무대 조명";
        window.UpdateLayout();
        Require(Math.Abs(Position(Tile(first.Id), 0, 0).Y - Position(Tile(second.Id), 0, 0).Y) < 1, "Two compact cards did not fit in one group row");
        Capture(window, Path.Combine(output, "lighting-groups-edit.png"));
        await Execute(vm, vm.CancelLightOrderCommand);
        Require(vm.LightGroups.All(g => g.IsDefault) && vm.Lights.Select(c => c.Id).SequenceEqual(original), "Cancel did not discard groups and order");
        // Repeat and save; deleting the draft group returns members without deleting a device.
        await Execute(vm, vm.EditLightOrderCommand);
        vm.NewLightGroupName = "무대 조명"; await Execute(vm, vm.AddLightGroupCommand);
        group = vm.LightGroups.Single(g => !g.IsDefault); window.UpdateLayout();
        zone = Zone(group.Id); Drag(first.Id, zone, 30, zone.ActualHeight - 20, -1);
        await Execute(vm, group.RemoveCommand);
        Require(vm.LightGroups.Single().Cards.Count == 4 && vm.Devices.Count == 5, "Group deletion deleted devices");
        vm.NewLightGroupName = "무대 조명"; await Execute(vm, vm.AddLightGroupCommand);
        group = vm.LightGroups.Single(g => !g.IsDefault); window.UpdateLayout();
        zone = Zone(group.Id); Drag(first.Id, zone, 30, zone.ActualHeight - 20, -1);
        Drag(second.Id, Tile(first.Id), 4, 50, 99);
        Require(vm.Jobs.Count == jobCount, "A drag or group edit sent a power job");
        await Execute(vm, vm.SaveLightOrderCommand);
        var (observer, _) = await host.Login();
        var state = await HostProcess.Until(observer, s => s.LightLayout.Groups.Length == 1);
        Require(state.LightLayout.Groups[0].Name == "무대 조명" &&
            state.LightLayout.Groups[0].DeviceIds.SequenceEqual(group.Cards.Select(c => c.Id)), "Group did not persist to HTTPS state");
        await Execute(vm, vm.EditLightOrderCommand);
        window.UpdateLayout(); start = Position(Tile(first.Id), 25, 30);
        Require(board.BeginCardDrag(first.Id, start, -1), "Lease-loss start failed");
        await Execute(vm, vm.ReleaseCommand);
        Require(!board.EndCardDrag(end, -1), "Pointer committed after use ended");
        await Execute(vm, vm.CancelLightOrderCommand); await Execute(vm, vm.AcquireCommand);
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
