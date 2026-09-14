using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class HiperwallCredentialStore(string dataPath) : ICredentialStore
{
    private string PathFor(Guid reference) => Path.Combine(dataPath, $"hiperwall-{reference:N}.dpapi");
    public Guid Save(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var reference = Guid.NewGuid();
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            using var file = new FileStream(PathFor(reference), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(encrypted); file.Flush(flushToDisk: true);
            return reference;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new DomainException("credential_store_unavailable", "지정 호스트 폴더와 실행 계정의 보호 저장 접근을 확인하세요.", 503); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Read(Guid reference)
    {
        byte[]? bytes = null;
        try
        {
            bytes = ProtectedData.Unprotect(File.ReadAllBytes(PathFor(reference)), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new DomainException("credential_store_unavailable", "저장된 Hiperwall 토큰을 읽을 수 없습니다. 호스트 실행 계정·원래 데이터 폴더를 확인하세요.", 503); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
