using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using IntegratedContro.Application;

namespace IntegratedContro.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class HostCertificate(ISecretProtector protector)
{
    public string Create(string directory, string bindAddress)
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
                protector.Protect(pfx));
            File.WriteAllBytes(Path.Combine(directory, "host-certificate.cer"), certificate.Export(X509ContentType.Cert));
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }
    public X509Certificate2 Load(string directory)
    {
        var pfx = protector.Unprotect(File.ReadAllBytes(Path.Combine(directory, "host-certificate.dpapi")));
        // Windows Schannel cannot use ephemeral keys. UserKeySet uses the OS-protected user key store; no PersistKeySet.
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
