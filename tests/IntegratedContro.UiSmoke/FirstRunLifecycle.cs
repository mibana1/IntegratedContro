using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IntegratedContro.App;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

internal static class FirstRunLifecycle
{
    static string Output = "";
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(50);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("UI condition timed out"); await Task.Delay(50); }
    }
    static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Output, name + ".png")); png.Save(file);
    }
    public static async Task Run(string scenario)
    {
        Output = Path.GetDirectoryName(ClientPreferences.ProfilePath)!;
        Directory.CreateDirectory(Output);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "IntegratedContro.sln"))) repo = repo.Parent;
        var exe = Path.Combine(repo!.FullName, "src", "IntegratedContro.ControlHost", "bin", "Debug", "net10.0", "win-x64", "IntegratedContro.ControlHost.exe");
        exe = SetupTestPaths.Host(exe);
        var config = StartupConfiguration.ForApp();
        await using var fixture = new HostProcess();
        if (scenario is "remote" or "existing") { await fixture.Initialize(); if (scenario == "existing") await fixture.Kill(); }
        if (scenario == "broken") File.WriteAllText(ClientPreferences.ProfilePath, "{invalid");
        using var log = new StringWriter(); using var listener = new TextWriterTraceListener(log);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var lifetime = new LocalServerLifetime();
        var prompts = 0;
        Task<string> Prepare() => scenario == "unavailable" ? Task.FromResult("격리 검사: 서버 준비 실패") : LocalServerStartup.StartForAppAsync(_ => { prompts++; return Task.FromResult(scenario != "decline"); }, lifetime);
        var main = new MainWindow(prepareServers: Prepare, stopServers: lifetime.StopAsync);
        var vm = (MainViewModel)main.DataContext;
        var password = (Environment.GetCommandLineArgs().Contains("--legacy-password-fixture") ? "legacy-admin-" : "한글-관리자-") + Guid.NewGuid().ToString("N");
        try
        {
            main.Show();
            if (scenario == "broken")
            {
                await Wait(() => main.LoginDialog?.IsVisible == true);
                Require(main.InitialSetupDialog is null && vm.HasPreferencesRecovery, "Damaged profile offered new initialization");
                Require(File.ReadAllText(ClientPreferences.ProfilePath) == "{invalid", "Damaged profile changed");
                main.LoginDialog!.Close(); await Wait(() => !main.IsVisible); return;
            }
            await Wait(() => main.InitialSetupDialog?.IsVisible == true);
            var setup = main.InitialSetupDialog!;
            Require(main.LoginDialog is null && setup.Model.IsChoosingMode, "Fresh startup did not show choices first");
            Require(prompts == 0 && !Directory.Exists(config.DefaultDataPath), "First screen launched servers or created data");
            Require(!setup.Model.SaveCommand.CanExecute(null), "Choice page can submit hidden defaults");
            Capture(setup, "choices");
            setup.Width = 620; setup.Height = 480; Capture(setup, "choices-small");
            setup.Width = 700; setup.Height = 800;
            Click(setup, "ConnectExistingServer"); setup.UpdateLayout();
            Require(setup.Model.IsRemote && ((TextBox)setup.FindName("RemoteEndpoint")).IsVisible, "Server choice failed");
            setup.Model.ShowChoices(); Click(setup, "UseExistingData"); setup.UpdateLayout();
            Require(setup.Model.IsLocal && !setup.Model.IsNew && !((PasswordBox)setup.FindName("AdminPassword")).IsVisible, "Data choice offers account creation");
            setup.Model.ShowChoices(); Click(setup, "StartNew");
            ((PasswordBox)setup.FindName("AdminPassword")).Password = password;
            setup.Model.ShowChoices();
            Require(((PasswordBox)setup.FindName("AdminPassword")).Password == "", "Going back retained password");
            if (scenario == "remote")
            {
                Click(setup, "ConnectExistingServer");
                setup.Model.Endpoint = fixture.Endpoint; setup.Model.Fingerprint = fixture.Fingerprint;
                Capture(setup, "server");
            }
            else
            {
                Click(setup, scenario == "existing" ? "UseExistingData" : "StartNew");
                // Use the selected Debug or isolated installed host; never launch an operational build.
                var data = scenario == "existing" ? fixture.DataPath : config.DefaultDataPath;
                config.Save(new LocalServerStartupSettings(data, "", "", "") { MediaMtxEnabled = false, ControlHostExecutablePath = exe });
                setup.Model.DataPath = data;
                setup.Model.MediaEnabled = false; // These lifecycle cases deliberately cover a host without video.
                if (scenario != "existing")
                {
                    using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
                    setup.Model.Port = ((IPEndPoint)tcp.LocalEndpoint).Port.ToString(); tcp.Stop();
                    setup.Model.SiteName = "첫 실행 격리 확인"; setup.Model.Administrator = "first-admin";
                    Capture(setup, "new-form");
                    ((PasswordBox)setup.FindName("AdminPassword")).Password = password;
                    ((PasswordBox)setup.FindName("ConfirmPassword")).Password = password;
                }
                else Capture(setup, "existing-data");
            }
            setup.Model.SaveCommand.Execute(null);
            await Wait(() => !setup.Model.IsBusy);
            Require(!setup.IsVisible, "Setup did not complete: " + setup.Model.Message);
            Require(((PasswordBox)setup.FindName("AdminPassword")).Password == "" && ((PasswordBox)setup.FindName("ConfirmPassword")).Password == "", "Password controls not cleared");
            Require(!File.ReadAllText(ClientPreferences.ProfilePath).Contains(password) && !File.ReadAllText(config.SettingsPath).Contains(password), "Password persisted");
            if (scenario == "new")
            {
                Require(vm.IsLoggedIn && vm.IsAdmin && main.LoginDialog is null && !vm.CanControl, "New administrator did not connect in view mode: " + vm.Message);
                Require(prompts == 1, "Server approval flow not preserved");
                Capture(main, "connected");
                main.Close(); await Wait(() => !main.IsVisible);
                var againLifetime = new LocalServerLifetime();
                var again = new MainWindow(prepareServers: () => LocalServerStartup.StartForAppAsync(_ => Task.FromResult(true), againLifetime), stopServers: againLifetime.StopAsync);
                try
                {
                    again.Show(); await Wait(() => again.LoginDialog?.IsVisible == true);
                    Require(again.InitialSetupDialog is null && !((MainViewModel)again.DataContext).IsLoggedIn, "Returning user repeated setup or retained password");
                    Capture(again.LoginDialog!, "returning-login");
                    again.LoginDialog!.Close(); await Wait(() => !again.IsVisible);
                }
                finally { again.Close(); await againLifetime.StopAsync(); }
            }
            else
            {
                await Wait(() => main.LoginDialog?.IsVisible == true);
                Require(!vm.IsLoggedIn, "Existing/declined path auto-authenticated");
                if (scenario is "decline" or "unavailable")
                {
                    Require(vm.Message.Contains("저장되었습니다") && File.Exists(Path.Combine(config.DefaultDataPath, "control.sqlite")), "Declining startup lost completed setup");
                    Capture(main.LoginDialog!, "declined-login");
                }
                else
                {
                    vm.LoginName = "admin";
                    ((PasswordBox)main.LoginDialog!.FindName("LoginPassword")).Password = fixture.Password;
                    vm.LoginCommand.Execute(null); await Wait(() => !vm.IsBusy);
                    Require(vm.IsLoggedIn && vm.IsAdmin, "Existing account could not log in: " + vm.Message);
                    if (scenario == "remote") Require(!Directory.Exists(config.DefaultDataPath) && !config.Read()!.Enabled && prompts == 0, "Remote choice created local server");
                }
                main.LoginDialog?.Close(); main.Close(); await Wait(() => !main.IsVisible);
                if (scenario == "remote") Require(fixture.Process is { HasExited: false }, "Existing server was stopped");
            }
            Require(log.ToString().Length == 0, "WPF binding warnings: " + log);
        }
        finally
        {
            main.InitialSetupDialog?.Close(); main.LoginDialog?.Close(); main.Close();
            await lifetime.StopAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
