using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.Tests;

public sealed class EnvironmentAdapterTests
{
    private static PlatformEnvironment Supported => new(true, "Client", "25H2", "Professional", "X64", "X64", "10.0.26200.9999");

    [Theory]
    [InlineData("Professional", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Core", false)]
    [InlineData("EnterpriseS", false)]
    public void Support_policy_is_evaluated_from_values_without_registry_access(string edition, bool supported)
    {
        IPlatformEnvironment detector = new FakePlatform(Supported with { Edition = edition });
        var result = PlatformPolicy.Evaluate(detector.Read());
        Assert.Equal(supported, result.OfficiallySupported);
        Assert.Contains("기능/실장비 검증과 별개", result.Description);
    }

    [Fact]
    public void Future_versions_servers_emulation_and_failed_detection_are_not_official_support()
    {
        PlatformEnvironment[] outside = [
            Supported with { IsWindows = false },
            Supported with { InstallationType = "Server" },
            Supported with { DisplayVersion = "24H2" },
            Supported with { DisplayVersion = "26H2" },
            Supported with { OsArchitecture = "Arm64" },
            Supported with { ProcessArchitecture = "X86" },
            Supported with { DetectionSucceeded = false }
        ];
        Assert.All(outside, environment => Assert.False(PlatformPolicy.Evaluate(environment).OfficiallySupported));
        // Cumulative update build numbers do not require a new support rule.
        Assert.True(PlatformPolicy.Evaluate(Supported with { Build = "10.0.26200.1" }).OfficiallySupported);
    }

    [Fact]
    public void Credential_repositories_use_injected_protection_and_clear_plaintext_buffers()
    {
        using var rig = new Rig();
        var protector = new RecordingProtector();
        var hiperwall = new HiperwallCredentialStore(rig.DirectoryPath, protector);
        var media = new MediaCredentialStore(rig.DirectoryPath, protector);
        var first = hiperwall.Save("fixture-token");
        Assert.All(protector.LastPlaintext!, b => Assert.Equal((byte)0, b));
        Assert.Equal("fixture-token", hiperwall.Read(first));
        Assert.All(protector.LastPlaintext!, b => Assert.Equal((byte)0, b));
        var second = media.Save("fixture-media");
        Assert.Equal("fixture-media", media.Read(second));
        Assert.All(protector.LastPlaintext!, b => Assert.Equal((byte)0, b));
        Assert.DoesNotContain("fixture-token", File.ReadAllText(Path.Combine(rig.DirectoryPath, $"hiperwall-{first:N}.dpapi")));
        media.Delete(second);
        Rig.Reject("media_secret_unavailable", () => media.Read(second));
    }

    [Fact]
    public void Failed_protection_does_not_save_plaintext_or_fall_back_to_another_store()
    {
        using var rig = new Rig();
        var protector = new RecordingProtector { Fail = true };
        var hiperwall = new HiperwallCredentialStore(rig.DirectoryPath, protector);
        var media = new MediaCredentialStore(rig.DirectoryPath, protector);
        Rig.Reject("credential_store_unavailable", () => hiperwall.Save("fixture-token"));
        Rig.Reject("media_secret_unavailable", () => media.Save("fixture-media"));
        Assert.Empty(Directory.GetFiles(rig.DirectoryPath, "*.dpapi"));
        Assert.All(protector.LastPlaintext!, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void Existing_dpapi_files_remain_readable_and_new_files_keep_current_user_format()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new Rig();
        var id = Guid.NewGuid();
        var legacyBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes("legacy-fixture"), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(rig.DirectoryPath, $"hiperwall-{id:N}.dpapi"), legacyBytes);
        File.WriteAllBytes(Path.Combine(rig.DirectoryPath, $"media-{id:N}.dpapi"), legacyBytes);
        var protector = new WindowsCurrentUserSecretProtector();
        var hiperwall = new HiperwallCredentialStore(rig.DirectoryPath, protector);
        var media = new MediaCredentialStore(rig.DirectoryPath, protector);
        Assert.Equal("legacy-fixture", hiperwall.Read(id));
        Assert.Equal("legacy-fixture", media.Read(id));
        var created = media.Save("new-fixture");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(rig.DirectoryPath, $"media-{created:N}.dpapi")),
            null, DataProtectionScope.CurrentUser);
        try { Assert.Equal("new-fixture", Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public async Task Composed_storage_keeps_state_and_virtual_values_in_the_same_owned_database()
    {
        using var rig = new Rig();
        var device = rig.Device();
        var before = rig.Store.Load();
        rig.Store.Dispose();
        using (var storage = SqliteHostStorage.Open(rig.DirectoryPath))
        {
            Assert.Equal(before.SiteId, storage.State.Load().SiteId);
            Assert.Throws<IOException>(() => SqliteHostStorage.Open(rig.DirectoryPath));
            await storage.VirtualDevices.WriteAsync(device, DeviceOperation.Power, 1, default);
        }
        using var reopened = SqliteHostStorage.Open(rig.DirectoryPath);
        Assert.Equal(before.SiteId, reopened.State.Load().SiteId);
        Assert.Equal(before.Accounts[0].Id, reopened.State.Load().Accounts[0].Id);
        Assert.Equal(1, (await reopened.VirtualDevices.ReadAsync(device, default))[DeviceOperation.Power]);
    }

    private sealed class FakePlatform(PlatformEnvironment environment) : IPlatformEnvironment
    {
        public PlatformEnvironment Read() => environment;
    }

    // Test-only opaque token protection proves injection without depending on an OS or crypto implementation.
    private sealed class RecordingProtector : ISecretProtector
    {
        private readonly Dictionary<string, byte[]> _values = [];
        public bool Fail { get; init; }
        public byte[]? LastPlaintext { get; private set; }
        public byte[] Protect(byte[] plaintext)
        {
            LastPlaintext = plaintext;
            if (Fail) throw new CryptographicException("Injected failure");
            var token = Guid.NewGuid().ToString("N");
            _values[token] = plaintext.ToArray();
            return Encoding.ASCII.GetBytes(token);
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (Fail) throw new CryptographicException("Injected failure");
            LastPlaintext = _values[Encoding.ASCII.GetString(ciphertext)].ToArray();
            return LastPlaintext;
        }
    }
}
