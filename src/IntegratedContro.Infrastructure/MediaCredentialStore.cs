using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class MediaCredentialStore(string dataPath) : IMediaSecretStore
{
    private string PathFor(Guid id) => Path.Combine(dataPath, $"media-{id:N}.dpapi");
    public Guid Save(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var id = Guid.NewGuid();
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            using var file = new FileStream(PathFor(id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(encrypted); file.Flush(true); return id;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        { throw Failure(); }
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
        { throw Failure(); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(Guid reference)
    {
        try { File.Delete(PathFor(reference)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Failure(); }
    }
    private static DomainException Failure() => new("media_secret_unavailable",
        "영상 보호 저장소를 읽거나 정리할 수 없습니다. 지정 데이터 폴더와 호스트 Windows 실행 계정을 확인하세요.", 503);
}
