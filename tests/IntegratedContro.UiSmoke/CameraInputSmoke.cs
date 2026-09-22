using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunCameraInput()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false) { Width = 1180, Height = 860 };
        var vm = (MainViewModel)window.DataContext; var camera = vm.Cameras;
        var polls = 0;
        PropertyChangedEventHandler onRefresh = (_, e) => { if (e.PropertyName == nameof(camera.AppliedMedia)) polls++; };
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("CameraTab");
            await Wait(() => camera.CanConfigure && !camera.IsBusy);
            var view = (IntegratedContro.App.CameraView)window.FindName("CameraWorkspace");
            camera.PropertyChanged += onRefresh;
            camera.CameraName = "Click during polling";
            var clickPending = true; var clickAccepted = false; var draftChanges = 0;
            PropertyChangedEventHandler clickDuringPoll = (_, e) =>
            {
                if (e.PropertyName == nameof(camera.DraftSummary)) draftChanges++;
                if (e.PropertyName != nameof(camera.IsBusy) || !clickPending || !camera.IsBusy || !camera.CanConfigure) return;
                clickPending = false;
                clickAccepted = camera.NewCommand.CanExecute(null);
                camera.NewCommand.Execute(null);
            };
            camera.PropertyChanged += clickDuringPoll;
            try
            {
                await Wait(() => !clickPending && !camera.IsBusy);
                Require(clickAccepted && camera.CameraName == "" && draftChanges == 1,
                    "A click during background polling was ignored or executed more than once");
            }
            finally { camera.PropertyChanged -= clickDuringPoll; }

            async Task TypeAcrossPolls(Control input)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var deactivated = false;
                    EventHandler onDeactivated = (_, _) => deactivated = true;
                    window.Deactivated += onDeactivated;
                    try { await TypeDuringActiveWindow(input); return; }
                    catch (InvalidOperationException) when (deactivated && attempt < 2)
                    {
                        Console.WriteLine($"RETRY: {input.Name} test window was deactivated by the desktop; require a fresh uninterrupted input attempt.");
                    }
                    finally { window.Deactivated -= onDeactivated; }
                }
            }
            async Task TypeDuringActiveWindow(Control input)
            {
                if (input is TextBox textBox) textBox.Clear();
                else ((PasswordBox)input).Clear();
                await Wait(() => input.IsLoaded && input.IsVisible && input.IsEnabled);
                input.BringIntoView(); window.UpdateLayout(); window.Activate();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                input.Focus(); Keyboard.Focus(input);
                Require(input.IsKeyboardFocused, $"{input.Name}: could not establish initial keyboard focus");
                var startPolls = polls; var disabled = 0; var focusLost = 0;
                var buttons = FindAll<Button>(view).Where(b => b.IsVisible && b.IsEnabled).ToArray();
                var flickering = new HashSet<string>();
                DependencyPropertyChangedEventHandler onButtonEnabled = (sender, _) =>
                {
                    var button = (Button)sender;
                    if (!button.IsEnabled) flickering.Add(button.Content?.ToString() ?? button.Name);
                };
                foreach (var button in buttons) button.IsEnabledChanged += onButtonEnabled;
                DependencyPropertyChangedEventHandler onEnabled = (_, _) => { if (!input.IsEnabled) disabled++; };
                KeyboardFocusChangedEventHandler onFocus = (_, _) => focusLost++;
                input.IsEnabledChanged += onEnabled; input.LostKeyboardFocus += onFocus;
                try
                {
                    foreach (var digit in "123456789")
                    {
                        Require(input.IsKeyboardFocused && input.IsEnabled,
                            $"{input.Name}: input lost focus or became disabled during periodic refresh; windowActive={window.IsActive}, enabled={input.IsEnabled}, focused={input.IsKeyboardFocused}, canConfigure={camera.CanConfigure}, canControl={vm.CanControl}, disabledEvents={disabled}, focusLostEvents={focusLost}, catalogUpdates={polls - startPolls}");
                        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, input, digit.ToString()));
                        await Task.Delay(550);
                    }
                    var value = input is TextBox text ? text.Text : ((PasswordBox)input).Password;
                    Require(value == "123456789", $"{input.Name}: continuous input was interrupted");
                    Require(disabled == 0 && focusLost == 0 && input.IsKeyboardFocused,
                        $"{input.Name}: refresh disabled the editor or moved keyboard focus");
                    Require(polls - startPolls >= 2, $"{input.Name}: background polling did not continue");
                    Require(buttons.Length >= 3 && flickering.Count == 0,
                        "Camera buttons blinked during polling: " + string.Join(", ", flickering));
                    if (input is TextBox textInput) Require(textInput.CaretIndex == 9, "Typing caret moved during refresh");
                }
                finally
                {
                    input.IsEnabledChanged -= onEnabled; input.LostKeyboardFocus -= onFocus;
                    foreach (var button in buttons) button.IsEnabledChanged -= onButtonEnabled;
                }
            }

            var name = (TextBox)view.FindName("CameraNameInput");
            var rtsp = (PasswordBox)view.FindName("RtspInput");
            await TypeAcrossPolls(name); await TypeAcrossPolls(rtsp);
            var tabs = (TabControl)view.FindName("CameraEditorTabs");
            tabs.SelectedIndex = 1;
            var api = (TextBox)view.FindName("ApiEndpointInput");
            var password = (PasswordBox)view.FindName("HlsPasswordInput");
            await TypeAcrossPolls(api); await TypeAcrossPolls(password);

            var locked = 0;
            DependencyPropertyChangedEventHandler onLocked = (_, _) => { if (!password.IsEnabled) locked++; };
            password.IsEnabledChanged += onLocked;
            await CameraExecute(camera, camera.RefreshCommand);
            password.IsEnabledChanged -= onLocked;
            Require(locked > 0 && password.IsEnabled, "Explicit commands no longer lock the form while running");
            Require(camera.CameraName == "123456789" && camera.ApiEndpoint == "123456789",
                "Draft fields were replaced by catalog polling");
            await Execute(vm, vm.ReleaseCommand);
            Require(!password.IsEnabled && !camera.SaveCommand.CanExecute(null), "Lease release did not disable editing");
            await Execute(vm, vm.AcquireCommand); await Wait(() => camera.CanConfigure);
            await Execute(vm, vm.LogoutCommand);
            Require(password.Password == "" && rtsp.Password == "" && !camera.CanConfigure, "Logout did not clear protected inputs");

            var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "camera-input-result.txt"),
                $"PASS: continuous 123456789 via WPF text composition in camera name, RTSP, API endpoint and HLS password; {polls} catalog updates; stable buttons; click during polling executes once; focus, caret and input preserved; explicit command lock, lease release and logout cleanup preserved.\nIsolated HTTPS host; synthetic WPF input, not physical keyboard automation.\n");
            Console.WriteLine("PASS: camera registration and MediaMTX inputs retain keyboard focus across background refreshes.");
        }
        finally { camera.PropertyChanged -= onRefresh; await vm.CloseAsync(); window.Close(); await Task.Delay(100); }
    }
}
