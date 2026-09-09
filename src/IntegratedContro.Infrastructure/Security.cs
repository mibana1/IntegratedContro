using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using IntegratedContro.Application;

namespace IntegratedContro.Infrastructure;

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 600_000;
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
    public bool Verify(string password, string hash)
    {
        try
        {
            var parts = hash.Split(':');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var count) ||
                count is < 100000 or > 2000000) return false;
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), count, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}
[SupportedOSPlatform("windows")]
public static class HostCertificate
{
    public static string Create(string directory, string bindAddress)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=IntegratedContro ControlHost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        if (System.Net.IPAddress.TryParse(bindAddress, out var address) && !address.Equals(System.Net.IPAddress.Any) &&
            !address.Equals(System.Net.IPAddress.Loopback)) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        var usage = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usage, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "host-certificate.dpapi"),
                ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
            File.WriteAllBytes(Path.Combine(directory, "host-certificate.cer"), certificate.Export(X509ContentType.Cert));
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }
    public static X509Certificate2 Load(string directory)
    {
        var pfx = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(directory, "host-certificate.dpapi")), null, DataProtectionScope.CurrentUser);
        // Windows Schannel cannot use ephemeral keys. UserKeySet uses the OS-protected user key store; no PersistKeySet.
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
public static class PlatformPolicy
{
    public static string Describe()
    {
        if (!OperatingSystem.IsWindows()) return "공식 지원 외 OS (Windows 11 25H2 Pro/Enterprise x64 대상)";
        return DescribeWindows();
    }
    [SupportedOSPlatform("windows")]
    private static string DescribeWindows()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = key?.GetValue("EditionID")?.ToString();
        var version = key?.GetValue("DisplayVersion")?.ToString();
        var supported = Environment.Is64BitProcess && version == "25H2" && edition is "Professional" or "Enterprise";
        return $"{(supported ? "공식 지원 대상" : "공식 지원 범위 외")}: Windows {version} {edition} x{(Environment.Is64BitProcess ? 64 : 32)}; build {Environment.OSVersion.Version}. 기능/실장비 검증과 별개.";
    }
}
