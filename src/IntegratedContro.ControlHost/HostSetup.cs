using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;

namespace IntegratedContro.ControlHost;

public sealed record HostConfiguration(string BindAddress, int Port, int HeartbeatTimeoutSeconds, string CertificateSha256);
public static class HostSetup
{
    public static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    [SupportedOSPlatform("windows")]
    public static void Initialize(string[] args, string directory)
    {
        var bind = Option(args, "--bind") ?? "127.0.0.1";
        var port = int.Parse(Option(args, "--port") ?? "7443");
        var seconds = int.Parse(Option(args, "--heartbeat-timeout") ?? "15");
        if (!IPAddress.TryParse(bind, out _) || port is < 1024 or > 65535 || seconds is < 3 or > 300)
            throw new ArgumentException("bind IP, port(1024~65535), heartbeat-timeout(3~300초)을 확인하세요.");
        var site = Option(args, "--site") ?? Prompt("현장 이름: ");
        var admin = Option(args, "--admin") ?? Prompt("최초 관리자 계정 이름: ");
        Validation.Text(site, "현장 이름");
        var password = ReadPassword("최초 관리자 비밀번호 (12자 이상): ");
        var confirmation = ReadPassword("비밀번호 확인: ");
        if (password != confirmation) throw new ArgumentException("비밀번호 확인이 일치하지 않습니다.");
        IInitialAdministratorPolicy policy = new InitialAdministratorPolicy(new Pbkdf2PasswordHasher());
        var account = policy.Create(admin, password);
        using var store = new SqliteStateStore(directory, initialize: true);
        var certHash = HostCertificate.Create(store.DataPath, bind);
        var config = new HostConfiguration(bind, port, seconds, certHash);
        File.WriteAllText(Path.Combine(store.DataPath, "host.json"), JsonSerializer.Serialize(config, JsonDefaults.Options));
        var state = new HostState { SiteName = site, Initialized = true, Accounts = [account] };
        state.Audit.Add(new(DateTimeOffset.UtcNow, account.Id, "InitialAdministratorCreated", "로컬 최초 설정 완료"));
        store.Save(state);
        Console.WriteLine("최초 설정 완료. 같은 --data 경로로 run 명령을 실행하세요.");
        Console.WriteLine($"HTTPS 인증서 SHA-256: {certHash}");
        Console.WriteLine("다른 PC의 앱에는 호스트 주소와 위 지문을 전달하세요. 개인 키는 현재 Windows 계정 DPAPI로 보호됩니다.");
    }
    private static string Prompt(string label) { Console.Write(label); return Console.ReadLine() ?? ""; }
    private static string ReadPassword(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? ""; // Scripted setup uses stdin, never command-line secrets.
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
