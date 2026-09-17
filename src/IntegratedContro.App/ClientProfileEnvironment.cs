using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IntegratedContro.App;

// Windows profile location and named process lock. JSON validation/recovery remain in ClientPreferences.
public static class ClientProfileEnvironment
{
    public static string ProfilePath
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--profile-dir");
            var directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntegratedContro");
            return Path.Combine(directory, "client.json");
        }
    }
    // Multiple app instances share this profile. Serialize reads, replacement and backup selection.
    public static IDisposable AcquireProfile(string path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        var mutex = new Mutex(false, "Local\\IntegratedContro.ClientProfile." + key);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("다른 앱이 접속 설정을 사용 중입니다."); }
            catch (AbandonedMutexException) { /* The file is still validated after an interrupted writer. */ }
            return new ProfileAccess(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class ProfileAccess(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    public static T RetrySharing<T>(Func<T> action)
    {
        // Windows can briefly deny opens while ReplaceFile exchanges the file names.
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException error) when (attempt < 4 && (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(20 * (attempt + 1)); }
        }
    }
}
