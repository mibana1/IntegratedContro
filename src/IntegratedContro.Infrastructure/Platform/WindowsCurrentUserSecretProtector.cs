using System.Runtime.Versioning;
using System.Security.Cryptography;
using IntegratedContro.Application;

namespace IntegratedContro.Infrastructure;

// Preserve the original DPAPI CurrentUser + null entropy format for existing credentials and certificates.
[SupportedOSPlatform("windows")]
public sealed class WindowsCurrentUserSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
