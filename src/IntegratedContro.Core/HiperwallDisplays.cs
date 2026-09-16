namespace IntegratedContro.Core;

public enum DisplayDurationMode { Default, Timed, Continuous }
public sealed record DisplayDuration(DisplayDurationMode Mode = DisplayDurationMode.Default, int? Seconds = null)
{
    public const int DefaultSeconds = 15;
    public bool IsValid => Enum.IsDefined(Mode) && (Mode == DisplayDurationMode.Timed ? Seconds is >= 1 and <= 86400 : Seconds is null);
    public int? EffectiveSeconds => Mode == DisplayDurationMode.Continuous ? null : Mode == DisplayDurationMode.Timed ? Seconds : DefaultSeconds;
    public string Label => Mode switch { DisplayDurationMode.Continuous => "계속 표시", DisplayDurationMode.Timed => $"{Seconds}초", _ => $"기본 {DefaultSeconds}초" };
}
public sealed record HiperwallPlacement(string Selector, string ContentValue, string ZoneId, HiperwallLayout Layout,
    int? Volume = null, bool? Muted = null)
{
    public string Label => $"{ContentValue} · {ZoneId} · ({Layout.X:0.###}, {Layout.Y:0.###}) · {Layout.Width:0.###} × {Layout.Height:0.###}";
}
public sealed record SavedHiperwallLayout(Guid Id, int Version, int ConfigurationVersion, string Name,
    HiperwallPlacement[] Placements, DisplayDuration Duration, DateTimeOffset SavedAt);
public sealed record SaveHiperwallLayoutRequest(long Generation, Guid Id, int ExpectedVersion, int ConfigurationVersion,
    string Name, HiperwallPlacement[] Placements, DisplayDuration Duration);
public sealed record DeleteHiperwallLayoutRequest(long Generation, Guid Id, int ExpectedVersion);
public sealed record HiperwallDisplayRequest(Guid RequestId, long Generation, int ConfigurationVersion,
    Guid? LayoutId = null, int? LayoutVersion = null, HiperwallPlacement[]? TestPlacements = null);
public enum DisplayCleanupState { Tracking, Closing, Closed, NeedsReview }
public sealed class HiperwallDisplayTarget
{
    public required HiperwallWireCommand Command { get; init; }
    public HiperwallSendState OpenState { get; set; }
    public DisplayCleanupState CleanupState { get; set; }
    public bool OpenAttempted { get; set; }
    public int CleanupAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string Message { get; set; } = "표시 대기";
    public bool Outstanding => CleanupState != DisplayCleanupState.Closed;
}
public sealed class HiperwallDisplayJob
{
    public required HiperwallDisplayRequest Request { get; init; }
    public Guid? ScenarioJobId { get; init; }
    public int? ScenarioStepIndex { get; init; }
    public required SessionInfo Requester { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public required DisplayDuration Duration { get; init; }
    public DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? CloseAt { get; set; }
    public bool StopRequested { get; set; }
    public Guid? StoppedBy { get; set; }
    public string? StopperName { get; set; }
    public List<HiperwallDisplayTarget> Targets { get; init; } = [];
    public bool Outstanding => Targets.Any(t => t.Outstanding);
    public string Summary => $"{Name} · 표시 응답 {Targets.Count(t => t.OpenState == HiperwallSendState.Acknowledged)} / 정리 완료 {Targets.Count(t => !t.Outstanding)} / 확인 필요 {Targets.Count(t => t.CleanupState == DisplayCleanupState.NeedsReview || t.OpenState == HiperwallSendState.Unknown)} / 전체 {Targets.Count}";
    public string Schedule => StopRequested ? "표시 종료·정리 요청됨" : CloseAt is { } at ? $"종료 예정 {at.ToLocalTime():MM-dd HH:mm:ss}" : "자동 종료 없음 · 호스트 종료 시 정리";
}
public sealed record HiperwallDisplayView(SavedHiperwallLayout[] Layouts, HiperwallDisplayJob[] Jobs);
