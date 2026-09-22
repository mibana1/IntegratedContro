using System.IO;
using System.Net;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public enum InitialSetupState { NewInstallation, ExistingLocalData, RemoteServer, ConfigurationError }
public sealed record InitialSetupStatus(InitialSetupState State, string Title, string Description);
public sealed record LocalHostMetadata(string BindAddress, int Port, string CertificateSha256)
{
    public string Endpoint => new UriBuilder("https", BindAddress is "0.0.0.0" or "::" ? "localhost" : BindAddress, Port).Uri.GetLeftPart(UriPartial.Authority);
}

// Stable per-user configuration, separate from versioned binaries and host-owned data.
public sealed class StartupConfiguration(string profileDirectory, string appDirectory, string? explicitPath = null)
{
    public string SettingsPath { get; } = explicitPath ?? Path.Combine(profileDirectory, LocalServerStartup.FileName);
    public string DefaultDataPath => Path.Combine(profileDirectory, "HostData");
    public string AppDirectory { get; } = appDirectory;
    public string HostExecutable => Path.GetFullPath("../ControlHost/IntegratedContro.ControlHost.exe", AppDirectory);
    public static StartupConfiguration ForApp()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--server-startup");
        if (index >= 0 && (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("--server-startup 뒤에 설정 파일 경로를 지정하세요.");
        return new(Path.GetDirectoryName(ClientPreferences.ProfilePath)!, AppContext.BaseDirectory,
            index < 0 ? null : Path.GetFullPath(args[index + 1]));
    }

    public LocalServerStartupSettings? Read()
    {
        using var access = ClientProfileEnvironment.AcquireProfile(SettingsPath);
        if (ReadFile<LocalServerStartupSettings>(SettingsPath) is { } saved) return saved;
        // A missing current file with a backup is damage, not a fresh installation.
        if (File.Exists(SettingsPath + ".bak")) throw new InvalidDataException("서버 설정 파일이 없습니다. 백업 설정을 복구하세요.");
        if (explicitPath is not null) return null;
        var legacy = Path.Combine(AppDirectory, LocalServerStartup.FileName);
        if (ReadFile<LocalServerStartupSettings>(legacy) is not { } settings) return null;
        ValidateSettings(settings);
        // Legacy relative paths were resolved against this same App directory.
        Save(settings);
        return settings;
    }

    public void Save(LocalServerStartupSettings settings)
    {
        ValidateSettings(settings);
        using var access = ClientProfileEnvironment.AcquireProfile(SettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, settings, JsonDefaults.Options); stream.Flush(true); }
            if (File.Exists(SettingsPath))
            {
                var backup = SettingsPath + ".bak";
                try { ValidateSettings(ReadFile<LocalServerStartupSettings>(SettingsPath)!); }
                catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
                { backup = SettingsPath + ".damaged-" + Guid.NewGuid().ToString("N"); }
                File.Replace(temporary, SettingsPath, backup);
            }
            else File.Move(temporary, SettingsPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void RestoreBackup()
    {
        using var access = ClientProfileEnvironment.AcquireProfile(SettingsPath);
        var backup = ReadFile<LocalServerStartupSettings>(SettingsPath + ".bak") ?? throw new InvalidDataException("서버 설정 백업이 없습니다.");
        ValidateSettings(backup);
        Save(backup);
    }

    public InitialSetupStatus Inspect(ClientPreferencesLoadResult profile)
    {
        try
        {
            if (profile.RecoveryRequired) return Error(profile.Message);
            var settings = Read();
            if (settings is { Enabled: true })
            {
                ValidateSettings(settings);
                var host = ReadHost(settings.HostDataPath);
                if (!string.IsNullOrEmpty(profile.Preferences.Endpoint))
                {
                    var (uri, pin) = ClientPreferences.ValidateConnection(profile.Preferences.Endpoint, profile.Preferences.Fingerprint);
                    // A remote connection is intentional; it must not be replaced by local data.
                    if (!LocalServerStartup.IsLocal(uri)) return Remote();
                    if (uri.Port != host.Port || !Convert.ToHexString(pin).Equals(host.CertificateSha256, StringComparison.OrdinalIgnoreCase))
                        return Error("접속 정보와 로컬 데이터의 서버 정보가 다릅니다. 사용할 서버를 선택하고 설정을 저장하세요.");
                }
                return new(InitialSetupState.ExistingLocalData, "기존 로컬 데이터", $"데이터 위치: {settings.HostDataPath}\n계정과 작업 기록을 그대로 사용합니다.");
            }
            if (!string.IsNullOrEmpty(profile.Preferences.Endpoint)) return Remote();
            // Never describe a leftover/partial default data directory as a new site.
            if (Directory.Exists(DefaultDataPath) && Directory.EnumerateFileSystemEntries(DefaultDataPath).Any())
            {
                _ = ReadHost(DefaultDataPath);
                return new(InitialSetupState.ExistingLocalData, "기존 로컬 데이터 연결 필요", $"{DefaultDataPath}\n기존 데이터 연결에서 이 폴더를 선택하세요.");
            }
            return new(InitialSetupState.NewInstallation, "초기 설정 필요", "새 로컬 서버를 만들거나 기존 데이터 폴더 또는 원격 서버를 연결하세요.");
        }
        catch (Exception e) when (IsConfigurationFailure(e)) { return Error(FriendlyError(e)); }
    }
    private static InitialSetupStatus Remote() => new(InitialSetupState.RemoteServer, "원격 / 별도 서버 접속", "운영 데이터는 접속한 서버에 저장됩니다. 이 PC에는 접속·화면 설정만 저장합니다.");
    private static InitialSetupStatus Error(string message) => new(InitialSetupState.ConfigurationError, "설정 오류 · 확인 필요", message + "\n기존 데이터는 초기화하지 않습니다.");
    public static bool IsConfigurationFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException;
    public static string FriendlyError(Exception e) => e is JsonException ? "설정 JSON 형식이 올바르지 않습니다." : e is UnauthorizedAccessException ? "설정 또는 데이터 폴더의 접근 권한을 확인하세요." : e.Message;

    public static T? ReadFile<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 65536) throw new InvalidDataException("설정 파일이 너무 큽니다.");
            return JsonSerializer.Deserialize<T>(stream, JsonDefaults.Options) ?? throw new InvalidDataException("설정 파일이 비어 있습니다.");
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }

    public static void ValidateSettings(LocalServerStartupSettings settings)
    {
        if (settings is null) throw new InvalidDataException("서버 설정이 비어 있습니다.");
        if (!settings.Enabled) return;
        ValidateLocalPath(settings.HostDataPath);
        if (string.IsNullOrWhiteSpace(settings.ControlHostExecutablePath)) throw new InvalidDataException("제어 서버 실행 파일 경로가 없습니다.");
        if (!settings.MediaMtxEnabled) return;
        if (string.IsNullOrWhiteSpace(settings.MediaMtxExecutablePath)) throw new InvalidDataException("영상 서버 실행 파일 경로가 없습니다.");
        ValidateLocalPath(settings.MediaMtxConfigurationPath);
        if (!Uri.TryCreate(settings.MediaMtxApiEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.AbsolutePath != "/" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new InvalidDataException("영상 서버 API는 http://127.0.0.1:포트 형식으로 지정하세요.");
    }

    public static string ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.StartsWith("//") || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("고정 로컬 디스크의 절대 경로를 선택하세요.");
        var full = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed) throw new ArgumentException("고정 로컬 디스크를 선택하세요.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("링크 또는 재분석 지점 경로는 사용할 수 없습니다.");
        return full;
    }
    public string ValidateDataPath(string path, bool create)
    {
        var full = ValidateLocalPath(path);
        var install = Path.GetFullPath(Path.Combine(AppDirectory, ".."));
        if (Under(full, install) || Under(install, full)) throw new ArgumentException("데이터 폴더는 앱 설치 폴더와 겹치지 않는 위치로 선택하세요.");
        if (create && Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new InvalidDataException("새 서버는 빈 폴더에만 만들 수 있습니다. 기존 파일이 있으면 기존 데이터 연결을 선택하세요.");
        return full;
    }
    private static bool Under(string path, string parent) => path.TrimEnd(Path.DirectorySeparatorChar).Equals(parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static LocalHostMetadata ReadHost(string data)
    {
        ValidateLocalPath(data);
        var host = ReadFile<LocalHostMetadata>(Path.Combine(data, "host.json")) ?? throw new InvalidDataException("host.json이 없습니다. 기존 데이터 폴더를 확인하세요.");
        if (!IPAddress.TryParse(host.BindAddress, out _) || host.Port is < 1024 or > 65535) throw new InvalidDataException("호스트 수신 주소 또는 포트가 올바르지 않습니다.");
        _ = ClientPreferences.ValidateConnection(host.Endpoint, host.CertificateSha256);
        foreach (var name in new[] { "control.sqlite", "host-certificate.dpapi", "host-certificate.cer" })
        {
            using var file = new FileStream(Path.Combine(data, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length == 0) throw new InvalidDataException($"{name} 파일이 비어 있습니다. 기존 데이터를 복구하세요.");
        }
        using (var database = new FileStream(Path.Combine(data, "control.sqlite"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Span<byte> header = stackalloc byte[16];
            if (database.Read(header) != 16 || !header.SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidDataException("운영 DB 형식이 올바르지 않습니다. 기존 데이터를 복구하세요.");
        }
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(Path.Combine(data, "host-certificate.cer"));
        if (!certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).Equals(host.CertificateSha256, StringComparison.OrdinalIgnoreCase) ||
            DateTime.UtcNow > certificate.NotAfter.ToUniversalTime() || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime())
            throw new InvalidDataException("호스트 인증서 지문 또는 유효기간을 확인하세요.");
        return host;
    }
}
