using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IntegratedContro.App;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunInitialSetup()
    {
        var root = Path.Combine(Path.GetDirectoryName(ClientPreferences.ProfilePath)!, "initial-setup-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile", "client.json");
        var config = new StartupConfiguration(Path.GetDirectoryName(profile)!, Path.Combine(root, "install", "App"));
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        InitialSetupWindow? window = null;
        try
        {
            window = new InitialSetupWindow(config, profile);
            var shown = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => window.ShowDialog());
            await Wait(() => window.IsVisible);
            var model = window.Model;
            Require(model.Status.State == InitialSetupState.NewInstallation && model.IsNew, "Fresh setup state not visible");
            Require(((TextBox)window.FindName("DataFolder")).IsVisible && ((PasswordBox)window.FindName("AdminPassword")).IsVisible, "New host controls missing");
            Require(!Directory.Exists(config.DefaultDataPath), "Opening setup created host data");
            Require(model.MediaEnabled && model.IsAutomaticMedia && ((CheckBox)window.FindName("EnableMedia")).IsVisible &&
                ((CheckBox)window.FindName("EnableMedia")).IsChecked == true, "Fresh host did not visibly default to automatic video setup");
            window.UpdateLayout();
            Capture(window, Path.Combine(root, "initial-setup-new.png"));
            // This validation fixture has no bundled executables and deliberately omits video.
            ((CheckBox)window.FindName("EnableMedia")).IsChecked = false;
            ((ComboBox)window.FindName("SetupMode")).SelectedIndex = 1;
            window.UpdateLayout();
            Require(model.IsLocal && !model.IsNew && !((PasswordBox)window.FindName("AdminPassword")).IsVisible, "Existing data offered administrator creation");
            ((Button)window.FindName("SaveSetup")).Command.Execute(null);
            await Wait(() => !model.IsBusy);
            Require(window.IsVisible && model.Message.Contains("host.json"), "Missing data was silently initialized or dialog closed");
            Capture(window, Path.Combine(root, "initial-setup-error.png"));
            ((ComboBox)window.FindName("SetupMode")).SelectedIndex = 2;
            window.UpdateLayout();
            Require(!((TextBox)window.FindName("DataFolder")).IsVisible && ((TextBox)window.FindName("RemoteEndpoint")).IsVisible, "Remote mode exposes local setup");
            model.Endpoint = "http://invalid"; model.Fingerprint = new string('A', 64);
            model.SaveCommand.Execute(null);
            await Wait(() => !model.IsBusy);
            Require(window.IsVisible && model.Message.Contains("https://"), "Invalid remote URL accepted");
            model.Endpoint = "https://192.0.2.12:7443";
            Capture(window, Path.Combine(root, "initial-setup-remote.png"));
            model.SaveCommand.Execute(null);
            await Wait(() => !window.IsVisible);
            Require(await shown == true, "Successful setup did not return to login");
            Require(config.Inspect(ClientPreferences.ReadForStartup(profile)).State == InitialSetupState.RemoteServer, "Remote settings not persisted");
            Require(bindingLog.ToString().Length == 0, "Initial setup binding errors: " + bindingLog);
            Console.WriteLine("PASS: initial setup WPF new/existing/remote/error modes, folder visibility, validation, save and close; " + root);
        }
        finally
        {
            window?.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
