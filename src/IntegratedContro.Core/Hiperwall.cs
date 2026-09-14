namespace IntegratedContro.Core;

public enum HiperwallAuthentication { None, Token }
public enum HiperwallConnectionState { NotConfigured, NotChecked, Checking, Connected, AuthenticationFailed, TimedOut, ConnectionFailed, Unsupported }
public enum HiperwallListState { NotQueried, Available, Unsupported, Failed }

// Only an opaque protected-store reference is persisted with ordinary host settings.
public sealed record HiperwallConfiguration(int Version, string Name, string Endpoint,
    HiperwallAuthentication Authentication, string User, int TimeoutMs, Guid? CredentialId);
public sealed record HiperwallSettingsView(int Version, string Name, string Endpoint,
    HiperwallAuthentication Authentication, string User, int TimeoutMs, bool HasSecret);
public sealed record SaveHiperwallRequest(long Generation, int ExpectedVersion, string Name, string Endpoint,
    HiperwallAuthentication Authentication, string User, int TimeoutMs, string? NewSecret = null);
public sealed record HiperwallController(string Version, string Authentication, string Role);
// Id is absent when not supplied. A UUID-less content may be opened only by its unique original full name.
public sealed record HiperwallItem(string? Id, string Name, IReadOnlyDictionary<string, string> Fields);
public sealed record HiperwallList(HiperwallListState State, string Reason, HiperwallItem[] Items,
    DateTimeOffset? SucceededAt = null);
public sealed record HiperwallReading(HiperwallConnectionState State, string Message, HiperwallController? Controller,
    HiperwallList Walls, HiperwallList Zones, HiperwallList Contents)
{
    public HiperwallList Instances { get; init; } = HiperwallLabels.NotQueried("열린 콘텐츠 조회 전입니다.");
}
public sealed record HiperwallView(int ConfigurationVersion, string ConnectionName, string Endpoint,
    HiperwallConnectionState State, string Message, HiperwallController? Controller, DateTimeOffset? LastSuccessAt,
    HiperwallList Walls, HiperwallList Zones, HiperwallList Contents)
{
    public HiperwallList Instances { get; init; } = HiperwallLabels.NotQueried("열린 콘텐츠 조회 전입니다.");
}

public static class HiperwallLabels
{
    public static string State(HiperwallConnectionState state) => state switch
    {
        HiperwallConnectionState.NotChecked => "연결 확인 전",
        HiperwallConnectionState.NotConfigured => "설정되지 않음", HiperwallConnectionState.Checking => "연결 확인 중",
        HiperwallConnectionState.Connected => "연결 성공", HiperwallConnectionState.AuthenticationFailed => "인증 실패",
        HiperwallConnectionState.TimedOut => "응답 시간 초과", HiperwallConnectionState.ConnectionFailed => "연결 실패",
        _ => "지원되지 않는 API 또는 응답 형식"
    };
    public static HiperwallList NotQueried(string reason = "새로 고침이 필요합니다.") => new(HiperwallListState.NotQueried, reason, []);
}
