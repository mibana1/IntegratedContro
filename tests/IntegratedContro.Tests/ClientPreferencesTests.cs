using System.Text.Json;
using System.Text.Json.Nodes;
using IntegratedContro.App;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

// Compile the UI-independent profile store directly; do not bring WPF into service tests.
public sealed class ClientPreferencesTests
{
    private readonly string _directory;
    private string Profile => Path.Combine(_directory, "client.json");
    private static ClientPreferences Example => new(Guid.NewGuid(), "https://127.0.0.1:9443", new string('A', 64)) { LastLoginName = "operator" };
    public ClientPreferencesTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IntegratedContro.sln"))) root = root.Parent;
        _directory = Path.Combine(root!.FullName, "artifacts", "preferences-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_directory);
    }
    [Fact]
    public void First_run_and_legacy_profile_keep_stable_pc_identity()
    {
        var first = ClientPreferences.ReadForStartup(Profile);
        Assert.False(first.RecoveryRequired); Assert.NotEqual(Guid.Empty, first.Preferences.PcId);
        Assert.Equal(first.Preferences, ClientPreferences.ReadForStartup(Profile).Preferences);
        var legacy = Example;
        File.WriteAllText(Profile, JsonSerializer.Serialize(new { legacy.PcId, legacy.Endpoint, legacy.Fingerprint }, JsonDefaults.Options));
        var read = ClientPreferences.ReadForStartup(Profile);
        Assert.False(read.RecoveryRequired); Assert.Equal("", read.Preferences.LastLoginName); Assert.Equal(legacy.PcId, read.Preferences.PcId);
    }
    [Theory]
    [InlineData("")]
    [InlineData("{ interrupted")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"pcId\":\"bad-guid\"}")]
    public void Broken_json_opens_recovery_without_changing_original(string json)
    {
        File.WriteAllText(Profile, json);
        var loaded = ClientPreferences.ReadForStartup(Profile);
        Assert.True(loaded.RecoveryRequired); Assert.Null(loaded.Backup);
        Assert.Equal("", loaded.Preferences.Endpoint); Assert.Equal(json, File.ReadAllText(Profile));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Theory]
    [InlineData("pcId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("endpoint", null)]
    [InlineData("endpoint", "http://127.0.0.1:9443")]
    [InlineData("endpoint", "https://user:password@127.0.0.1")]
    [InlineData("endpoint", "https://127.0.0.1/path")]
    [InlineData("fingerprint", "bad-pin")]
    [InlineData("lastLoginName", null)]
    public void Invalid_fields_are_not_used_as_connection_settings(string property, string? value)
    {
        var data = JsonSerializer.SerializeToNode(Example, JsonDefaults.Options)!; data[property] = value;
        var json = data.ToJsonString(); File.WriteAllText(Profile, json);
        var loaded = ClientPreferences.ReadForStartup(Profile);
        Assert.True(loaded.RecoveryRequired); Assert.Equal("", loaded.Preferences.Endpoint);
        Assert.Equal(json, File.ReadAllText(Profile));
    }
    [Fact]
    public void Normal_save_keeps_last_valid_backup_and_no_partial_temporary_file()
    {
        var first = Example; first.Save(Profile);
        var second = first with { LastLoginName = "second" }; second.Save(Profile);
        Assert.Equal(second, ClientPreferences.ReadForStartup(Profile).Preferences);
        Assert.Equal(first, ClientPreferences.ReadForStartup(Profile + ".bak").Preferences);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
    [Fact]
    public void Backup_is_only_applied_after_explicit_recovery_and_damaged_original_is_preserved()
    {
        var original = Example; original.Save(Profile);
        (original with { LastLoginName = "next" }).Save(Profile);
        var backupBytes = File.ReadAllBytes(Profile + ".bak");
        File.WriteAllText(Profile, "{ damaged");
        var read = ClientPreferences.ReadForStartup(Profile);
        Assert.True(read.RecoveryRequired); Assert.Equal(original, read.Backup); Assert.Equal(original, read.Preferences);
        Assert.Equal("{ damaged", File.ReadAllText(Profile));
        Assert.Equal(original, ClientPreferences.RestoreBackup(Profile));
        Assert.False(ClientPreferences.ReadForStartup(Profile).RecoveryRequired);
        Assert.Equal(original, ClientPreferences.ReadForStartup(Profile).Preferences);
        Assert.Equal(backupBytes, File.ReadAllBytes(Profile + ".bak"));
        Assert.Equal("{ damaged", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "client.json.damaged-*.json"))));
    }
    [Fact]
    public void Missing_primary_offers_backup_instead_of_generating_another_pc_id()
    {
        var backup = Example; backup.Save(Profile + ".bak");
        var read = ClientPreferences.ReadForStartup(Profile);
        Assert.True(read.RecoveryRequired); Assert.Equal(backup, read.Preferences); Assert.False(File.Exists(Profile));
        Assert.Equal(backup, ClientPreferences.RestoreBackup(Profile));
    }
    [Fact]
    public void Bad_backup_is_ignored_when_primary_is_valid_and_revalidated_at_restore()
    {
        var current = Example; current.Save(Profile);
        File.WriteAllText(Profile + ".bak", "{ bad");
        Assert.False(ClientPreferences.ReadForStartup(Profile).RecoveryRequired);
        Assert.Throws<JsonException>(() => ClientPreferences.RestoreBackup(Profile));
        Assert.Equal(current, ClientPreferences.ReadForStartup(Profile).Preferences);
        Assert.Equal("{ bad", File.ReadAllText(Profile + ".bak"));
    }
    [Fact]
    public void Manual_recovery_preserves_damaged_files()
    {
        File.WriteAllText(Profile, "null"); File.WriteAllText(Profile + ".bak", "{ bad backup");
        var read = ClientPreferences.ReadForStartup(Profile);
        var replacement = read.Preferences with { Endpoint = Example.Endpoint, Fingerprint = Example.Fingerprint };
        replacement.Save(Profile);
        Assert.Equal(replacement, ClientPreferences.ReadForStartup(Profile).Preferences);
        Assert.Equal("null", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "client.json.damaged-*.json"))));
        Assert.Equal("{ bad backup", File.ReadAllText(Profile + ".bak"));
    }
    [Fact]
    public void Locked_file_allows_recovery_ui_but_failed_save_preserves_primary_and_backup()
    {
        var first = Example; first.Save(Profile); (first with { LastLoginName = "latest" }).Save(Profile);
        var original = File.ReadAllBytes(Profile); var backup = File.ReadAllBytes(Profile + ".bak");
        using (var held = new FileStream(Profile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(ClientPreferences.ReadForStartup(Profile).RecoveryRequired);
            Assert.Throws<IOException>(() => first.Save(Profile));
        }
        Assert.Equal(original, File.ReadAllBytes(Profile)); Assert.Equal(backup, File.ReadAllBytes(Profile + ".bak"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
    [Fact]
    public void Failed_replacement_does_not_delete_original()
    {
        var value = Example; value.Save(Profile);
        var original = File.ReadAllBytes(Profile); Directory.CreateDirectory(Profile + ".bak");
        Assert.ThrowsAny<IOException>(() => (value with { LastLoginName = "replacement" }).Save(Profile));
        Assert.Equal(original, File.ReadAllBytes(Profile)); Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
    [Fact]
    public void Unusable_directory_does_not_block_startup_or_choose_another_location()
    {
        var blocked = Path.Combine(_directory, "blocked"); File.WriteAllText(blocked, "keep");
        var read = ClientPreferences.ReadForStartup(Path.Combine(blocked, "client.json"));
        Assert.True(read.RecoveryRequired); Assert.Equal("keep", File.ReadAllText(blocked));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact]
    public void Oversized_files_and_values_do_not_replace_working_settings()
    {
        var value = Example; value.Save(Profile);
        Assert.Throws<InvalidDataException>(() => (value with { LastLoginName = new string('x', 70000) }).Save(Profile));
        Assert.Equal(value, ClientPreferences.ReadForStartup(Profile).Preferences);
        File.WriteAllText(Profile, new string('x', 70000));
        Assert.True(ClientPreferences.ReadForStartup(Profile).RecoveryRequired);
        Assert.Equal(70000, new FileInfo(Profile).Length);
    }
    [Fact]
    public async Task Concurrent_saves_and_reads_never_observe_partial_json()
    {
        var value = Example; value.Save(Profile);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 30; i++) (value with { LastLoginName = "revision-" + i }).Save(Profile);
        });
        for (var i = 0; i < 100; i++)
        {
            var read = ClientPreferences.ReadForStartup(Profile);
            Assert.False(read.RecoveryRequired); Assert.Equal(value.PcId, read.Preferences.PcId);
            await Task.Yield();
        }
        await writer;
        Assert.Equal("revision-29", ClientPreferences.ReadForStartup(Profile).Preferences.LastLoginName);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
    [Fact]
    public void Interrupted_temporary_write_is_not_selected_on_restart()
    {
        var value = Example; value.Save(Profile);
        File.WriteAllText(Profile + ".interrupted.tmp", "{ unfinished");
        Assert.Equal(value, ClientPreferences.ReadForStartup(Profile).Preferences);
        Assert.False(ClientPreferences.ReadForStartup(Profile).RecoveryRequired);
    }
}