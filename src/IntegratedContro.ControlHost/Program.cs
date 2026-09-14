using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using IntegratedContro.Application;
using IntegratedContro.ControlHost;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("초기 호스트는 Windows x64용입니다."); return 1;
}
var dataPath = HostSetup.Option(args, "--data");
if (string.IsNullOrWhiteSpace(dataPath))
{
    Console.Error.WriteLine("사용법: IntegratedContro.ControlHost.exe setup|run|backup|verify-backup --data <로컬 절대 폴더>");
    Console.Error.WriteLine("복원: IntegratedContro.ControlHost.exe restore --from <백업 폴더> --data <새 빈 로컬 폴더>");
    Console.Error.WriteLine("setup 옵션: --site <현장> --admin <계정> --bind <수신 IP> --port <포트> --heartbeat-timeout <초>");
    return 2;
}
try
{
    if (args.FirstOrDefault() == "setup")
    {
        HostSetup.Initialize(args, dataPath); return 0;
    }
    if (args.FirstOrDefault() == "verify-backup")
    {
        var manifest = SqliteStateStore.VerifyBackup(dataPath);
        Console.WriteLine($"백업 검증 완료: 현장 {manifest.SiteId}, DB v{manifest.DatabaseVersion}, revision {manifest.Revision}");
        return 0;
    }
    if (args.FirstOrDefault() == "restore")
    {
        var source = HostSetup.Option(args, "--from") ?? throw new ArgumentException("--from 백업 폴더를 지정하세요.");
        SqliteStateStore.RestoreBackup(source, dataPath);
        Console.WriteLine("새 폴더 복원 완료. 원 호스트를 중지한 뒤 이 폴더로 run하고 관리자 복구 검토·대상 상태 대조를 수행하세요.");
        return 0;
    }
    if (args.FirstOrDefault() == "backup")
    {
        using var backupStore = new SqliteStateStore(dataPath);
        Console.WriteLine($"백업 검증 완료: {backupStore.CreateBackup("manual").Directory}");
        return 0;
    }
    if (args.FirstOrDefault() != "run") throw new ArgumentException("setup, run, backup, verify-backup 또는 restore를 명시하세요.");
    using var store = new SqliteStateStore(dataPath);
    var config = JsonSerializer.Deserialize<HostConfiguration>(
        File.ReadAllText(Path.Combine(store.DataPath, "host.json")), JsonDefaults.Options)
        ?? throw new InvalidDataException("호스트 설정이 없습니다.");
    if (!IPAddress.TryParse(config.BindAddress, out var bind) || config.Port is < 1024 or > 65535)
        throw new InvalidDataException("호스트 IP/포트 설정 오류");
    using var certificate = HostCertificate.Load(store.DataPath);
    if (certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) != config.CertificateSha256 ||
        DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        throw new InvalidDataException("인증서 지문 또는 유효기간을 확인하세요.");
    using var hiperwallReader = new HiperwallHttpReader();
    var service = new ControlService(store, new Pbkdf2PasswordHasher(), new VirtualDeviceDriver(store.ConnectionString),
        heartbeatTimeoutSeconds: config.HeartbeatTimeoutSeconds, hiperwall: hiperwallReader,
        credentials: new HiperwallCredentialStore(store.DataPath));
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders(); builder.Logging.AddConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.Limits.MaxRequestBodySize = 256 * 1024;
        server.Listen(bind, config.Port, listener => listener.UseHttps(certificate));
    });
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton(service);
    builder.Services.AddHostedService<ControlWorker>();
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                $"{context.Connection.RemoteIpAddress}:{(context.Request.Path == "/api/login" ? "login" : "api")}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = context.Request.Path == "/api/login" ? 10 : 600,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });
    var app = builder.Build();
    app.UseRateLimiter();
    app.Use(async (context, next) =>
    {
        try { await next(); }
        catch (DomainException error)
        {
            context.Response.StatusCode = error.Status;
            await context.Response.WriteAsJsonAsync(new ApiError(error.Code, error.Message), JsonDefaults.Options);
        }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new ApiError("storage_unavailable", "지정 데이터 폴더/DB를 확인하세요. 신규 제어가 차단됩니다."));
        }
    });
    string Token(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : "";
    }
    app.MapGet("/health", () => new { status = "ready", mode = "Virtual", protocol = 1 });
    app.MapPost("/api/login", (LoginRequest request) => service.Login(request));
    app.MapGet("/api/hiperwall/settings", (HttpContext c) => service.GetHiperwallSettings(Token(c)));
    app.MapPost("/api/hiperwall/settings", (HttpContext c, SaveHiperwallRequest r) => service.SaveHiperwallSettings(Token(c), r));
    app.MapPost("/api/hiperwall/edit", (HttpContext c, HiperwallEditRequest r) => service.EditHiperwallAsync(Token(c), r, c.RequestAborted));
    app.MapGet("/api/hiperwall/edits", (HttpContext c) => service.GetHiperwallEdits(Token(c)));
    app.MapGet("/api/hiperwall/edits/{id:guid}", (HttpContext c, Guid id) => service.GetHiperwallEdit(Token(c), id));
    app.MapPost("/api/hiperwall/edits/cancel", (HttpContext c, JobActionRequest r) => service.CancelHiperwallEdit(Token(c), r));
    app.MapGet("/api/hiperwall/status", (HttpContext c) => service.GetHiperwallStatus(Token(c)));
    app.MapPost("/api/hiperwall/refresh", (Func<HttpContext, Task<HiperwallView>>)(c => service.RefreshHiperwallAsync(Token(c), false, c.RequestAborted)));
    app.MapPost("/api/hiperwall/test", (Func<HttpContext, Task<HiperwallView>>)(c => service.RefreshHiperwallAsync(Token(c), true, c.RequestAborted)));
    app.MapGet("/api/state", (HttpContext c) => service.GetState(Token(c)));
    app.MapPost("/api/history/jobs", (HttpContext c, HistoryRequest r) => service.GetJobHistory(Token(c), r));
    app.MapPost("/api/history/hiperwall", (HttpContext c, HistoryRequest r) => service.GetHiperwallHistory(Token(c), r));
    app.MapPost("/api/history/audit", (HttpContext c, HistoryRequest r) => service.GetAuditHistory(Token(c), r));
    app.MapPost("/api/backups", (HttpContext c) => service.CreateBackup(Token(c)));
    app.MapPost("/api/lease/acquire", (HttpContext c) => service.Acquire(Token(c)));
    app.MapPost("/api/lease/heartbeat", (HttpContext c, LeaseRequest r) => service.Heartbeat(Token(c), r.Generation));
    app.MapPost("/api/lease/release", (HttpContext c, LeaseRequest r) => service.Release(Token(c), r.Generation));
    app.MapPost("/api/logout", (HttpContext c) => service.Logout(Token(c)));
    app.MapPost("/api/accounts", (HttpContext c, CreateAccountRequest r) => service.CreateAccount(Token(c), r));
    app.MapPost("/api/accounts/permissions", (HttpContext c, UpdateAccountRequest r) => service.UpdateAccount(Token(c), r));
    app.MapPost("/api/devices", (HttpContext c, DeviceRequest r) => service.SaveDevice(Token(c), r));
    app.MapPost("/api/layout/lights", (HttpContext c, LightOrderRequest r) => service.SaveLightOrder(Token(c), r));
    app.MapPost("/api/roles", (HttpContext c, RoleRequest r) => service.SaveRole(Token(c), r));
    app.MapPost("/api/scenarios", (HttpContext c, ScenarioRequest r) => service.SaveScenario(Token(c), r));
    app.MapPost("/api/lights/power", (HttpContext c, LightBatchRequest r) => service.SubmitLightBatch(Token(c), r));
    app.MapPost("/api/jobs", (HttpContext c, SubmitRequest r) => service.Submit(Token(c), r));
    app.MapPost("/api/jobs/cancel", (HttpContext c, JobActionRequest r) => service.Cancel(Token(c), r));
    app.MapPost("/api/jobs/manual-switch", (HttpContext c, JobActionRequest r) => service.BeginManualSwitch(Token(c), r));
    app.MapPost("/api/devices/reconcile", (HttpContext c, ReconcileRequest r) => service.ReconcileAsync(Token(c), r, c.RequestAborted));
    app.MapPost("/api/recovery/review", (HttpContext c) => service.ReviewRecovery(Token(c)));
    app.MapPost("/api/recovery/approve", (HttpContext c, RecoveryApprovalRequest r) => service.ApproveRecovery(Token(c), r.ReviewId));
    app.Lifetime.ApplicationStopping.Register(service.StopAccepting);
    Console.WriteLine(PlatformPolicy.Describe());
    Console.WriteLine($"가상 장비 호스트: https://{config.BindAddress}:{config.Port}");
    Console.WriteLine($"데이터 폴더: {store.DataPath}");
    Console.WriteLine($"인증서 SHA-256: {config.CertificateSha256}");
    await app.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"호스트 시작/설정 실패 ({error.GetType().Name}): {error.Message}");
    Console.Error.WriteLine("Worker와 신규 제어를 시작하지 않았습니다. 지정 데이터 경로·실행 계정·기존 설정을 확인하세요.");
    return 1;
}

public sealed class ControlWorker(ControlService service) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Dispatch(stoppingToken), Watch(stoppingToken), EditHiperwall(stoppingToken));
    private async Task Dispatch(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await service.DispatchNextAsync(ct);
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
    private async Task EditHiperwall(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await service.DispatchHiperwallNextAsync(ct);
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
    private async Task Watch(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            if (!ct.IsCancellationRequested) service.CheckConnections();
        }
    }
}
