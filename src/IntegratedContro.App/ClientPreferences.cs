using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record ClientPreferencesLoadResult(ClientPreferences Preferences, bool RecoveryRequired = false,
    ClientPreferences? Backup = null, string Message = "");

public sealed record ClientPreferences(Guid PcId, string Endpoint, string Fingerprint)
{
    private const int MaximumBytes = 64 * 1024;
    public string LastLoginName { get; init; } = "";
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
    public static ClientPreferences Load() => ReadForStartup().Preferences;
    public static ClientPreferencesLoadResult ReadForStartup(string? path = null)
    {
        try
        {
            path ??= ProfilePath;
            using var access = AcquireProfile(path);
            if (Read(path) is { } current) return new(current);
            if (ReadBackup(path) is { } backup) return Recovery(backup);
            if (File.Exists(path + ".bak")) return Recovery(null);
            var initial = new ClientPreferences(Guid.NewGuid(), "", "");
            initial.Save(path);
            return new(initial);
        }
        catch (Exception error) when (Recoverable(error))
        {
            // Keep damaged/unreadable files untouched until the user chooses recovery.
            return Recovery(path is null ? null : ReadBackup(path));
        }
    }
    private static ClientPreferencesLoadResult Recovery(ClientPreferences? backup) =>
        new(backup ?? new(Guid.NewGuid(), "", ""), true, backup,
            backup is not null
                ? "이 PC의 접속 설정을 읽을 수 없습니다. 백업 내용을 확인해 복구하거나 주소와 지문을 다시 저장하세요."
                : "이 PC의 접속 설정을 읽을 수 없으며 유효한 백업도 없습니다. 주소와 지문을 다시 저장하거나 파일 접근 문제를 해결한 뒤 다시 읽으세요.");
    private static bool Recoverable(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static ClientPreferences? ReadBackup(string path)
    {
        try { return Read(path + ".bak"); }
        catch (Exception error) when (Recoverable(error)) { return null; }
    }
    private static ClientPreferences? Read(string path) => RetrySharing(() => ReadOnce(path));
    private static ClientPreferences? ReadOnce(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("접속 설정 파일이 허용 크기를 초과합니다.");
            var value = JsonSerializer.Deserialize<ClientPreferences>(stream, JsonDefaults.Options)
                ?? throw new InvalidDataException("접속 설정 파일이 비어 있습니다.");
            value.Validate();
            return value;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    // Multiple app instances share this profile. Serialize reads, replacement and backup selection.
    private static IDisposable AcquireProfile(string path)
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
    private static T RetrySharing<T>(Func<T> action)
    {
        // Windows can briefly deny opens while ReplaceFile exchanges the file names.
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException error) when (attempt < 4 && (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(20 * (attempt + 1)); }
        }
    }
    private void Validate()
    {
        if (PcId == Guid.Empty || Endpoint is null || Fingerprint is null || LastLoginName is null)
            throw new InvalidDataException("접속 설정의 필수 항목을 확인하세요.");
        if (Endpoint == "" && Fingerprint == "") return; // Supported first-run/legacy empty profile.
        try { _ = ValidateConnection(Endpoint, Fingerprint); }
        catch (ArgumentException error) { throw new InvalidDataException("접속 주소 또는 인증서 지문이 올바르지 않습니다.", error); }
    }
    internal static (Uri Uri, byte[] Pin) ValidateConnection(string endpoint, string fingerprint)
    {
        if (endpoint is null || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("https://호스트주소:포트 형식으로 입력하세요.");
        byte[] pin;
        try { pin = Convert.FromHexString((fingerprint ?? "").Replace(" ", "").Replace(":", "").Trim()); }
        catch (FormatException) { throw new ArgumentException("호스트 설정에 표시된 SHA-256 지문을 입력하세요."); }
        if (pin.Length != 32) throw new ArgumentException("SHA-256 지문은 64자리 16진수입니다.");
        return (uri, pin);
    }
    public static ClientPreferences RestoreBackup(string? path = null)
    {
        path ??= ProfilePath;
        using var access = AcquireProfile(path);
        // Read again at the action boundary; an old UI indication must not authorize an invalid backup.
        var backup = Read(path + ".bak") ?? throw new InvalidDataException("복구할 백업이 없습니다. 설정을 다시 입력하세요.");
        backup.Save(path, preserveBackup: true);
        return backup;
    }
    public void Save(string? path = null) => Save(path ?? ProfilePath, preserveBackup: false);
    private void Save(string path, bool preserveBackup)
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("접속 설정이 허용 크기를 초과합니다.");
        path = Path.GetFullPath(path);
        using var access = AcquireProfile(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            bool exists;
            var valid = false;
            try { exists = Read(path) is not null; valid = exists; }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            { exists = true; }
            if (exists)
            {
                // Never remove the original first. Replacement and old-file preservation are one operation.
                var backup = valid && !preserveBackup ? path + ".bak" :
                    path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json";
                RetrySharing(() => { File.Replace(temporary, path, backup); return true; });
            }
            else File.Move(temporary, path); // Do not overwrite a profile another process just created.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}