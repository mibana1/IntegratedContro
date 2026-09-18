using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunThemeSwitching()
    {
        var preferencePath = AppearancePreferences.SettingsPath;
        var original = File.Exists(preferencePath) ? File.ReadAllBytes(preferencePath) : null;
        new AppearancePreferences().Save();
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); window.Appearance.Reload();
        var vm = (MainViewModel)window.DataContext;
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
        Color Palette(string key) => ColorOf((Brush)System.Windows.Application.Current.FindResource(key));
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            foreach (var name in new[] { "입구 조명", "회의실 조명" })
            {
                await Execute(vm, vm.DeviceSettings.NewDeviceCommand);
                vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
                vm.DeviceSettings.DeviceName = name; vm.DeviceSettings.ConnectionId = "theme-fixture";
                await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            }
            var profileBefore = File.ReadAllBytes(ClientPreferences.ProfilePath);
            var button = (Button)window.FindName("ThemeToggleButton");
            var tabs = (TabControl)window.FindName("MainTabs");
            var selectedTab = tabs.SelectedItem;
            vm.DeviceControl.CommandValueText = "37";
            Require(!window.Appearance.IsDark && (string)button.Content == "다크 모드", "Initial light appearance/action label incorrect");
            var light = ColorOf(window.Background);
            await Click(vm, button); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Require(window.Appearance.IsDark && AppearancePreferences.Load().IsDark && (string)button.Content == "라이트 모드",
                "Account button did not switch and save dark mode");
            Require(ColorOf(window.Background) == Palette("AppBackground") && ColorOf(window.Background) != light,
                "Existing window did not update its palette");
            Require(tabs.SelectedItem == selectedTab && vm.DeviceControl.CommandValueText == "37" && vm.JobManagement.Jobs.Count == 0,
                "Changing appearance lost selection/draft or dispatched work");
            Require(File.ReadAllBytes(ClientPreferences.ProfilePath).SequenceEqual(profileBefore),
                "Appearance changed connection credentials/identity");
            foreach (var (fg, bg) in new[] { ("Ink", "Surface"), ("MutedInk", "Surface"), ("OnAccent", "AccentFill"),
                ("Accent", "AccentSoft"), ("DangerInk", "DangerSoft"), ("LightOnInk", "LightOnSoft") })
                Require(Contrast(Palette(fg), Palette(bg)) >= 4.5, $"Dark palette contrast: {fg}/{bg}");
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            var position = button.TransformToAncestor(window).Transform(new Point());
            Require(button.ActualHeight >= 48 && position.X > window.ActualWidth / 2 &&
                position.X + button.ActualWidth < window.ActualWidth && position.Y < 110, "Account theme action clipped or misplaced");
            Capture(window, Path.Combine(output, "theme-dark-lighting.png"));
            tabs.SelectedItem = window.FindName("MyInfoTab"); window.UpdateLayout();
            var identity = (Expander)window.FindName("AccountIdentityExpander");
            identity.IsExpanded = true; window.UpdateLayout();
            var identityHeader = FindAll<ToggleButton>(identity).Single();
            Require(Contrast(ColorOf(identityHeader.Foreground), Palette("SubtleSurface")) >= 4.5 &&
                FindAll<TextBlock>(identity).Where(t => t.IsVisible).All(t =>
                    Contrast(ColorOf(t.Foreground), Palette("Surface")) >= 4.5),
                "Account/session identity header or details are unreadable in dark mode");
            Capture(window, Path.Combine(output, "theme-dark-account-identity.png"));
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i; window.UpdateLayout();
                Capture(window, Path.Combine(output, $"theme-dark-tab-{i + 1}.png"));
            }
            tabs.SelectedItem = window.FindName("AdminTab"); window.UpdateLayout();
            var scrolling = FindAll<ScrollViewer>(window).First(s => s.ScrollableHeight > 100 && s.ViewportHeight > 100);
            scrolling.ScrollToTop(); window.UpdateLayout();
            var bar = (ScrollBar)scrolling.Template.FindName("PART_VerticalScrollBar", scrolling);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 60) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 60, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Require(scrolling.VerticalOffset > 0, "The themed scrollbar thumb no longer scrolls its content");
            tabs.SelectedIndex = 0; vm.DeviceViewIndex = 1; window.UpdateLayout();
            var input = (TextBox)window.FindName("ManualNumericValue");
            Require(ColorOf(input.Foreground) == Palette("Ink") && ColorOf(input.Background) == Palette("Surface"),
                "Input palette failed after switching tabs");
            var combo = (ComboBox)window.FindName("ControlCapabilityPicker");
            Require(ColorOf(combo.Background) == Palette("Surface"), "Combo box stayed light");
            var grid = (DataGrid)window.FindName("DeviceGrid");
            Require(ColorOf(grid.Foreground) == Palette("Ink") && ColorOf(grid.RowBackground) == Palette("Surface") &&
                ColorOf(grid.AlternatingRowBackground) == Palette("AlternateRow"), "Table palette stayed light");
            await Click(vm, button); window.UpdateLayout();
            Require(!window.Appearance.IsDark && !AppearancePreferences.Load().IsDark && ColorOf(window.Background) == light,
                "Round trip did not restore light mode");
            tabs.SelectedItem = window.FindName("MyInfoTab"); window.UpdateLayout();
            Require(ColorOf(identityHeader.Foreground) == Palette("Ink") &&
                FindAll<TextBlock>(identity).Where(t => t.IsVisible).All(t =>
                    Contrast(ColorOf(t.Foreground), Palette("Surface")) >= 4.5),
                "Account/session identity did not restore readable light-mode text");
            tabs.SelectedIndex = 0; window.UpdateLayout();
            Capture(window, Path.Combine(output, "theme-light-restored.png"));
            await Click(vm, button);
            // Save failure must leave connection data intact and clearly describe session-only appearance.
            using (var locked = new FileStream(preferencePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Click(vm, button);
                Require(!window.Appearance.IsDark && window.Appearance.SaveError.Length > 0,
                    "A failed appearance save was presented as persistent");
            }
            Require(AppearancePreferences.Load().IsDark && File.ReadAllBytes(ClientPreferences.ProfilePath).SequenceEqual(profileBefore),
                "Failed appearance save corrupted settings");
            await Click(vm, button);
            Require(window.Appearance.SaveError == "" && AppearancePreferences.Load().IsDark, "Theme save did not recover");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--theme-startup-child"); start.ArgumentList.Add("--profile-dir");
            start.ArgumentList.Add(Path.GetDirectoryName(preferencePath)!);
            using (var child = Process.Start(start)!)
            {
                try
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Require(child.ExitCode == 0, "Saved dark mode did not survive production startup: " + await child.StandardError.ReadToEndAsync());
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
            var damaged = Path.Combine(output, "appearance-invalid.json");
            await File.WriteAllTextAsync(damaged, "{broken");
            Require(!AppearancePreferences.Load(damaged).IsDark, "Malformed appearance did not use a safe light default");
            Require(await File.ReadAllTextAsync(damaged) == "{broken", "Reading invalid appearance rewrote it");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Theme binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "theme-switching-result.txt"),
                "PASS: account-header button at minimum window size; live dark/light round trip; selected tab and draft retained; no job or connection-profile write; inputs/table palette and readable text contrast; atomic save failure/recovery; real production startup restores dark mode including login; malformed settings fall back without rewriting. Isolated virtual host.");
            Console.WriteLine("Dark/light theme switching, persistence and production startup WPF smoke PASS.");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            await vm.CloseAsync(); window.Close();
            if (original is null) File.Delete(preferencePath); else File.WriteAllBytes(preferencePath, original);
            window.Appearance.Reload();
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte component) { var v = component / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static int RunThemeStartupChild()
    {
        var app = new IntegratedContro.App.App(); app.InitializeComponent();
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Wait(() => app.MainWindow is MainWindow { LoginDialog.IsVisible: true });
                var main = (MainWindow)app.MainWindow;
                Require(main.Appearance.IsDark && (string)((Button)main.FindName("ThemeToggleButton")).Content == "라이트 모드",
                    "Production startup ignored saved appearance");
                Require(((SolidColorBrush)main.Background).Color == ((SolidColorBrush)main.LoginDialog!.Background).Color,
                    "Login did not inherit dark appearance");
                Capture(main.LoginDialog!, Path.Combine(Path.GetDirectoryName(ClientPreferences.ProfilePath)!, "..", "theme-dark-login.png"));
                main.LoginDialog!.Close();
            }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        });
        return app.Run();
    }
}
