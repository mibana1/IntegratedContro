using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class HiperwallService : ICameraContentCatalog, IScenarioDisplayOperations
{
    private readonly IHiperwallStateAccess _host;
    private readonly IScenarioJobLifecycle _jobs;
    private readonly CancellationTokenSource _previewStopping = new();
    internal HiperwallService(IHiperwallStateAccess host, IHiperwallReader? reader, ICredentialStore? credentials, IScenarioJobLifecycle jobs)
    { _host = host; _hiperwall = reader; _credentials = credentials; _jobs = jobs; }
    internal bool ReadSupported => _hiperwall is not null;
    internal bool WriteSupported => _hiperwall is IHiperwallWriter;
    internal void Stop()
    {
        MarkDisplaysForShutdown(); _previewStopping.Cancel();
        foreach (var query in _hiperwallQueries.Values) query.Cancel();
    }
    public void ValidateMapping(int configurationVersion, string? selector, string? value)
    {
        if (string.IsNullOrEmpty(selector) && string.IsNullOrEmpty(value)) return;
        Require(_host.Current.Hiperwall?.Version == configurationVersion &&
            _hiperwallView?.ConfigurationVersion == configurationVersion &&
            _hiperwallView.Contents.State == HiperwallListState.Available,
            "mapping_inventory_required", "현재 Hiperwall Contents를 조회하고 매핑 대상을 선택하세요.");
        Require(PreviewMatches(_hiperwallView!.Contents, selector ?? "", value ?? ""),
            "mapping_not_unique", "현재 목록에서 유일한 UUID 또는 전체 이름만 매핑할 수 있습니다.", 400);
    }
    private readonly IHiperwallReader? _hiperwall;
    private readonly ICredentialStore? _credentials;
    private readonly Dictionary<Guid, CancellationTokenSource> _hiperwallQueries = [];
    private HiperwallView? _hiperwallView;
    private long _hiperwallSequence;

    private Session HiperwallReaderSession(string token)
    {
        _host.Healthy();
        var session = _host.Authenticate(token);
        Require(!_host.IsFenced(session.Info.Id), "session_fenced", "이전 세션이 차단되었습니다. 다시 로그인하세요.", 403);
        // Current account policy grants every enabled authenticated role site-wide read access.
        return session;
    }
    private static HiperwallSettingsView Settings(HiperwallConfiguration c) =>
        new(c.Version, c.Name, c.Endpoint, c.Authentication, c.User, c.TimeoutMs, c.CredentialId is not null);
    private HiperwallView EmptyHiperwall(HiperwallConnectionState? state = null, string? message = null)
    {
        var c = _host.Current.Hiperwall;
        var status = state ?? (c is null ? HiperwallConnectionState.NotConfigured : HiperwallConnectionState.NotChecked);
        return new(c?.Version ?? 0, c?.Name ?? "", c?.Endpoint ?? "", status,
            message ?? (c is null ? "관리자가 연결 설정을 저장해야 합니다." : "저장된 설정입니다. 아직 연결을 확인하지 않았습니다."),
            null, null, HiperwallLabels.NotQueried(), HiperwallLabels.NotQueried(), HiperwallLabels.NotQueried());
    }
    public HiperwallSettingsView GetHiperwallSettings(string token)
    {
        using (_host.Open())
        {
            HiperwallReaderSession(token); _host.Admin(_host.Current, token);
            return _host.Current.Hiperwall is { } c ? Settings(c) : new(0, "", "", HiperwallAuthentication.Token, "", 3000, false);
        }
    }
    public HiperwallView GetHiperwallStatus(string token)
    {
        using (_host.Open()) { HiperwallReaderSession(token); return JsonDefaults.Copy(_hiperwallView ?? EmptyHiperwall()); }
    }
    public HiperwallSettingsView SaveHiperwallSettings(string token, SaveHiperwallRequest request)
    {
        using (_host.Open())
        {
            _host.Healthy(); _host.CheckConnections();
            var session = _host.Owner(_host.Current, token, request.Generation); _host.Admin(_host.Current, token);
            Require(_hiperwall is not null && _credentials is not null, "hiperwall_unavailable", "호스트의 Hiperwall 어댑터를 확인하세요.", 503);
            Text(request.Name, "연결 이름");
            Require(request.Endpoint is { Length: <= 2048 } &&
                Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var parsed) &&
                parsed.Scheme is "http" or "https" && parsed.AbsolutePath == "/" &&
                parsed.UserInfo == "" && parsed.Query == "" && parsed.Fragment == "" &&
                System.Text.RegularExpressions.Regex.IsMatch(request.Endpoint, @"^https?://(\[[^\]]+\]|[^/:\s]+):[0-9]{1,5}/?$") &&
                parsed.Port is >= 1 and <= 65535,
                "invalid_endpoint", "주소·포트를 명시하세요: http(s)://주소:포트 (경로·인증정보 제외).", 400);
            Require(Enum.IsDefined(request.Authentication), "invalid_authentication", "None 또는 Token 인증을 선택하세요.", 400);
            Require(request.TimeoutMs is >= 100 and <= 30000, "invalid_timeout", "전체 조회 제한시간은 100~30000ms입니다.", 400);
            var old = _host.Current.Hiperwall;
            Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "다른 설정이 적용되었습니다. 적용 설정을 다시 불러오세요.");
            Guid? reference = null;
            var user = "";
            if (request.Authentication == HiperwallAuthentication.Token)
            {
                Text(request.User, "Hiperwall 사용자", 256); user = request.User.Trim();
                var canReuse = old?.Authentication == request.Authentication &&
                    old.Endpoint == request.Endpoint.TrimEnd('/') && old.User == user && old.CredentialId is not null;
                Require(request.NewSecret is { Length: > 0 } || canReuse,
                    "secret_required", "새 대상·사용자에는 앱에서 토큰을 입력하세요. 저장된 토큰은 반환하지 않습니다.", 400);
                if (!string.IsNullOrEmpty(request.NewSecret))
                {
                    Require(request.NewSecret.Length <= 4096 && !request.NewSecret.Any(char.IsControl),
                        "invalid_secret", "토큰은 제어 문자 없는 1~4096자여야 합니다.", 400);
                    // Save a new immutable protected blob BEFORE committing its reference.
                    // A failed aggregate commit leaves the previous reference usable, never overwritten.
                    reference = _credentials!.Save(request.NewSecret);
                }
                else reference = old!.CredentialId;
            }
            var next = _host.Draft();
            next.Hiperwall = new((old?.Version ?? 0) + 1, request.Name.Trim(), request.Endpoint.TrimEnd('/'),
                request.Authentication, user, request.TimeoutMs, reference);
            _host.Audit(next, session.Info.UserId, "HiperwallSettingsSaved", $"version={next.Hiperwall.Version}");
            _host.Commit(next);
            _hiperwallSequence++;
            foreach (var query in _hiperwallQueries.Values.ToArray()) query.Cancel();
            _hiperwallView = EmptyHiperwall();
            return Settings(next.Hiperwall);
        }
    }
    private static HiperwallList KeepLastTime(HiperwallList list, HiperwallList? previous) =>
        list with { SucceededAt = list.SucceededAt ?? previous?.SucceededAt };
    private HiperwallView FailedHiperwall(HiperwallConnectionState state, string reason, HiperwallView? previous)
    {
        var failed = new HiperwallList(HiperwallListState.Failed, reason, []);
        return EmptyHiperwall(state, reason) with { LastSuccessAt = previous?.LastSuccessAt,
            Walls = KeepLastTime(failed, previous?.Walls), Zones = KeepLastTime(failed, previous?.Zones),
            Contents = KeepLastTime(failed, previous?.Contents), Instances = KeepLastTime(failed, previous?.Instances) };
    }
    internal void CancelHiperwallSession(Guid sessionId)
    {
        if (_hiperwallQueries.TryGetValue(sessionId, out var query)) query.Cancel();
    }
    public async Task<HiperwallView> RefreshHiperwallAsync(string token, bool connectionTest, CancellationToken ct)
    {
        HiperwallConfiguration config;
        Session session;
        CancellationTokenSource query;
        long sequence;
        DateTimeOffset? previousSuccess;
        HiperwallView? previousView;
        using (_host.Open())
        {
            session = HiperwallReaderSession(token);
            if (connectionTest) _host.Admin(_host.Current, token);
            if (_host.Current.Hiperwall is null)
            {
                if (connectionTest)
                {
                    var next = _host.Draft();
                    _host.Audit(next, session.Info.UserId, "HiperwallConnectionTest", "version=0; result=NotConfigured");
                    _host.Commit(next);
                }
                return EmptyHiperwall();
            }
            Require(_hiperwall is not null && _credentials is not null, "hiperwall_unavailable", "호스트의 Hiperwall 어댑터를 확인하세요.", 503);
            Require(!_hiperwallQueries.ContainsKey(session.Info.Id), "hiperwall_busy", "이 세션의 조회가 진행 중입니다.");
            config = _host.Current.Hiperwall;
            query = CancellationTokenSource.CreateLinkedTokenSource(ct);
            query.CancelAfter(config.TimeoutMs);
            _hiperwallQueries.Add(session.Info.Id, query);
            sequence = ++_hiperwallSequence;
            previousView = _hiperwallView;
            previousSuccess = previousView?.LastSuccessAt;
            _hiperwallView = EmptyHiperwall(HiperwallConnectionState.Checking, "읽기 전용 연결·목록 확인 중입니다.") with { LastSuccessAt = previousSuccess };
        }
        try
        {
            // Protected storage and network work never hold the execution authority lock.
            var reading = await Task.Run(async () =>
            {
                string? secret = null;
                try
                {
                    if (config.CredentialId is { } reference) secret = _credentials!.Read(reference);
                    return await _hiperwall!.ReadAsync(config, secret, query.Token);
                }
                finally { secret = null; }
            }, query.Token).ConfigureAwait(false);
            using (_host.Open())
            {
                HiperwallReaderSession(token);
                Require(_host.Current.Hiperwall?.Version == config.Version, "hiperwall_settings_changed", "설정이 변경되어 이전 연결의 응답을 폐기했습니다.");
                query.Token.ThrowIfCancellationRequested();
                var success = reading.State == HiperwallConnectionState.Connected ? _host.Now : previousSuccess;
                var result = new HiperwallView(config.Version, config.Name, config.Endpoint, reading.State, reading.Message,
                    reading.Controller, success, KeepLastTime(reading.Walls, previousView?.Walls),
                    KeepLastTime(reading.Zones, previousView?.Zones), KeepLastTime(reading.Contents, previousView?.Contents))
                    { Instances = KeepLastTime(reading.Instances, previousView?.Instances) };
                if (sequence == _hiperwallSequence) _hiperwallView = result;
                if (connectionTest)
                {
                    var next = _host.Draft();
                    _host.Audit(next, session.Info.UserId, "HiperwallConnectionTest", $"version={config.Version}; result={reading.State}");
                    _host.Commit(next);
                }
                return JsonDefaults.Copy(result);
            }
        }
        catch (DomainException e) when (e.Code == "credential_store_unavailable")
        {
            using (_host.Open())
            {
                HiperwallReaderSession(token);
                Require(_host.Current.Hiperwall?.Version == config.Version, "hiperwall_settings_changed", "설정이 변경되어 이전 연결의 결과를 폐기했습니다.");
                var result = FailedHiperwall(HiperwallConnectionState.ConnectionFailed, e.Message, previousView);
                if (sequence == _hiperwallSequence) _hiperwallView = result;
                if (connectionTest)
                {
                    var next = _host.Draft();
                    _host.Audit(next, session.Info.UserId, "HiperwallConnectionTest", $"version={config.Version}; result=ConnectionFailed");
                    _host.Commit(next);
                }
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            using (_host.Open())
            {
                HiperwallReaderSession(token);
                Require(_host.Current.Hiperwall?.Version == config.Version, "hiperwall_settings_changed", "설정이 변경되어 이전 연결의 응답을 폐기했습니다.");
                var state = ct.IsCancellationRequested || _host.Stopping ? HiperwallConnectionState.ConnectionFailed : HiperwallConnectionState.TimedOut;
                var reason = ct.IsCancellationRequested ? "조회가 취소되었습니다. 다시 조회하세요." : "전체 조회 제한시간을 초과했습니다.";
                var result = FailedHiperwall(state, reason, previousView);
                if (sequence == _hiperwallSequence) _hiperwallView = result;
                if (connectionTest && !ct.IsCancellationRequested)
                {
                    var next = _host.Draft();
                    _host.Audit(next, session.Info.UserId, "HiperwallConnectionTest", $"version={config.Version}; result={state}");
                    _host.Commit(next);
                }
                return result;
            }
        }
        finally
        {
            using (_host.Open())
            {
                _hiperwallQueries.Remove(session.Info.Id);
                if (sequence == _hiperwallSequence && _hiperwallView?.State == HiperwallConnectionState.Checking)
                    _hiperwallView = EmptyHiperwall(HiperwallConnectionState.ConnectionFailed, "조회가 종료되었습니다. 권한·보호 저장 상태를 확인하고 다시 조회하세요.") with { LastSuccessAt = previousSuccess };
            }
            query.Dispose();
        }
    }
}
