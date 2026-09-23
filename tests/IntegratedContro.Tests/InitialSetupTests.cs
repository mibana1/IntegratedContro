using System.Text.Json;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class InitialSetupTests
{
    private static string Root()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "IntegratedContro.sln"))) repo = repo.Parent;
        var path = Path.Combine(repo!.FullName, "artifacts", "initial-setup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static ClientPreferences Empty() => new(Guid.NewGuid(), "", "");
    private static StartupConfiguration Config(string root, string version = "v1") =>
        new(Path.Combine(root, "profile"), Path.Combine(root, version, "App"));
    private static LocalServerStartupSettings Settings(string data, string exe = "../ControlHost/IntegratedContro.ControlHost.exe") =>
        new(data, "", "", "") { ControlHostExecutablePath = exe, MediaMtxEnabled = false };

    [Fact]
    public void Fresh_install_does_not_create_host_data_and_partial_data_is_an_error()
    {
        var config = Config(Root()); var profile = new ClientPreferencesLoadResult(Empty());
        Assert.Equal(InitialSetupState.NewInstallation, config.Inspect(profile).State);
        Assert.False(Directory.Exists(config.DefaultDataPath));
        Directory.CreateDirectory(config.DefaultDataPath);
        File.WriteAllText(Path.Combine(config.DefaultDataPath, "control.sqlite"), "incomplete");
        Assert.Equal(InitialSetupState.ConfigurationError, config.Inspect(profile).State);
        Assert.Equal("incomplete", File.ReadAllText(Path.Combine(config.DefaultDataPath, "control.sqlite")));
    }

    [Fact]
    public void Legacy_settings_migrate_once_and_current_profile_survives_a_new_build()
    {
        var root = Root(); var config = Config(root);
        Directory.CreateDirectory(config.AppDirectory);
        var legacy = Settings(Path.Combine(root, "data"));
        var oldPath = Path.Combine(config.AppDirectory, LocalServerStartup.FileName);
        File.WriteAllText(oldPath, JsonSerializer.Serialize(legacy, JsonDefaults.Options));
        Assert.Equal(legacy, config.Read());
        var updated = legacy with { HostDataPath = Path.Combine(root, "chosen-data") };
        config.Save(updated);
        Assert.Equal(updated, Config(root, "v2").Read());
        Assert.Equal(updated, config.Read()); // Old deployment must not overwrite current selection.
        Assert.True(File.Exists(oldPath));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Damaged_saved_settings_do_not_fall_back_to_legacy_or_new_data(string damaged)
    {
        var config = Config(Root()); Directory.CreateDirectory(Path.GetDirectoryName(config.SettingsPath)!);
        File.WriteAllText(config.SettingsPath, damaged);
        Assert.Equal(InitialSetupState.ConfigurationError, config.Inspect(new(Empty())).State);
        Assert.Equal(damaged, File.ReadAllText(config.SettingsPath));
        Assert.False(Directory.Exists(config.DefaultDataPath));
    }

    [Fact]
    public void Backup_is_explicit_and_corrupt_original_is_retained()
    {
        var config = Config(Root()); var settings = Settings(config.DefaultDataPath);
        config.Save(settings); config.Save(settings with { HostDataPath = settings.HostDataPath + "-second" });
        File.WriteAllText(config.SettingsPath, "{bad");
        config.RestoreBackup();
        Assert.Equal(settings, config.Read());
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(config.SettingsPath)!, "*.damaged-*"),
            path => File.ReadAllText(path) == "{bad");
    }

    [Fact]
    public void Data_location_rejects_relative_network_install_and_nonempty_paths()
    {
        var config = Config(Root());
        Assert.Throws<ArgumentException>(() => config.ValidateDataPath("relative", true));
        Assert.Throws<ArgumentException>(() => config.ValidateDataPath(@"\\server\data", true));
        Assert.Throws<ArgumentException>(() => config.ValidateDataPath(Path.Combine(config.AppDirectory, "data"), true));
        Directory.CreateDirectory(config.DefaultDataPath);
        File.WriteAllText(Path.Combine(config.DefaultDataPath, "unrelated.txt"), "keep");
        Assert.Throws<InvalidDataException>(() => config.ValidateDataPath(config.DefaultDataPath, true));
    }

    [Fact]
    public void Remote_settings_disable_local_startup_and_preserve_identity()
    {
        var root = Root(); var config = Config(root); var path = Path.Combine(root, "profile", "client.json");
        var profile = Empty() with { LastLoginName = "operator" };
        config.Save(Settings(Path.Combine(root, "missing")));
        new InitialSetupService(config, path).SaveRemote("https://192.0.2.10:7443", new string('A', 64), profile);
        var loaded = ClientPreferences.ReadForStartup(path);
        Assert.Equal(profile.PcId, loaded.Preferences.PcId);
        Assert.Equal("operator", loaded.Preferences.LastLoginName);
        Assert.Equal(InitialSetupState.RemoteServer, config.Inspect(loaded).State);
        Assert.False(config.Read()!.Enabled);
        Assert.False(Directory.Exists(config.DefaultDataPath));
    }

    [Fact]
    public async Task Existing_data_requires_stopped_host_and_preserves_accounts_site_and_history()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var root = Root(); var config = Config(root);
        var profile = Empty(); var path = Path.Combine(root, "profile", "client.json");
        config.Save(Settings(host.DataPath, host.Process!.MainModule!.FileName));
        Assert.Equal(InitialSetupState.ExistingLocalData, config.Inspect(new(new(profile.PcId, host.Endpoint, host.Fingerprint))).State);
        var service = new InitialSetupService(config, path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveLocalAsync(host.DataPath, false, "", "", "", "", "", "", false, "", "", profile));
        Assert.False(host.Process.HasExited);
        await host.Kill();
        HostState original;
        using (var storage = SqliteHostStorage.Open(host.DataPath)) original = storage.State.Load();
        await service.SaveLocalAsync(host.DataPath, false, "", "", "", "", "", "", false, "", "", profile);
        var saved = ClientPreferences.ReadForStartup(path);
        Assert.Equal(host.Fingerprint, saved.Preferences.Fingerprint);
        Assert.Equal(InitialSetupState.ExistingLocalData, config.Inspect(saved).State);
        using (var storage = SqliteHostStorage.Open(host.DataPath))
        {
            var after = storage.State.Load();
            Assert.Equal(original.SiteId, after.SiteId);
            Assert.Equal(original.Accounts.Select(a => a.Id), after.Accounts.Select(a => a.Id));
            Assert.Equal(original.Audit.Count, after.Audit.Count);
        }
        File.Move(Path.Combine(host.DataPath, "host-certificate.dpapi"), Path.Combine(host.DataPath, "certificate.saved"));
        Assert.Equal(InitialSetupState.ConfigurationError, config.Inspect(saved).State);
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveLocalAsync(host.DataPath, false, "", "", "", "", "", "", false, "", "", profile));
        Assert.True(File.Exists(Path.Combine(host.DataPath, "control.sqlite")));
    }

    [Fact]
    public async Task New_site_setup_creates_administrator_once_and_optional_media_does_not_launch()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var exe = host.Process!.MainModule!.FileName;
        var root = Root(); var config = Config(root); var path = Path.Combine(root, "profile", "client.json");
        config.Save(Settings(config.DefaultDataPath, exe));
        var profile = Empty();
        var service = new InitialSetupService(config, path);
        var password = Guid.NewGuid().ToString("N");
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString();
        await service.SaveLocalAsync(config.DefaultDataPath, true, "새 현장", "setup-admin", password, password, "127.0.0.1", port, false, "", "", profile);
        var saved = ClientPreferences.ReadForStartup(path);
        Assert.Equal(profile.PcId, saved.Preferences.PcId);
        Assert.Equal("setup-admin", saved.Preferences.LastLoginName);
        Assert.DoesNotContain(password, File.ReadAllText(path));
        Assert.Equal(InitialSetupState.ExistingLocalData, config.Inspect(saved).State);
        using (var storage = SqliteHostStorage.Open(config.DefaultDataPath))
        {
            var state = storage.State.Load();
            Assert.True(state.Initialized); Assert.Single(state.Accounts);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveLocalAsync(config.DefaultDataPath, true, "replace", "other", password, password, "127.0.0.1", "7443", false, "", "", profile));
        listener.Stop();
        IReadOnlyList<string>? requested = null;
        await LocalServerStartup.StartAsync(config.SettingsPath, config.AppDirectory, saved, names =>
        { requested = names; return Task.FromResult(false); });
        Assert.NotNull(requested); Assert.Single(requested); Assert.StartsWith("ControlHost", requested[0]);
    }

    [Fact]
    public async Task Invalid_database_and_protected_certificate_never_become_fresh_installation()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var exe = host.Process!.MainModule!.FileName; await host.Kill();
        var root = Root(); var config = Config(root); var path = Path.Combine(root, "profile", "client.json");
        config.Save(Settings(host.DataPath, exe));
        var service = new InitialSetupService(config, path);
        File.WriteAllText(Path.Combine(host.DataPath, "host-certificate.dpapi"), "corrupted");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveLocalAsync(host.DataPath, false, "", "", "", "", "", "", false, "", "", Empty()));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(host.DataPath, "control.sqlite")));
    }
}
