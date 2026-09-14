namespace IntegratedContro.Core;

public enum ScenarioStepKind { DeviceCommand, WaitUntil, ShowLayout }
public sealed record HiperwallLayoutEntry(string Selector, string ContentValue, string Name, string ZoneId,
    HiperwallLayout Bounds, int? Volume = null, bool? Muted = null);
public sealed record SavedHiperwallLayout(Guid Id, string Name, int Version, int ConfigurationVersion,
    string Endpoint, HiperwallLayoutEntry[] Entries);
public sealed record CaptureHiperwallLayoutRequest(long Generation, Guid Id, string Name, int ExpectedVersion,
    int ConfigurationVersion, string ExpectedInstancesRevision, string? FallbackZoneId = null);
public sealed record DeleteHiperwallLayoutRequest(long Generation, Guid Id, int ExpectedVersion);
public static class ScenarioLabels
{
    public static string Kind(ScenarioStepKind kind) => kind switch
    {
        ScenarioStepKind.WaitUntil => "조건 충족까지 대기",
        ScenarioStepKind.ShowLayout => "저장 배치 표시",
        _ => "장비 명령"
    };
    public static string Target(StepSnapshot step) => step.SavedLayout is { } layout
        ? $"Hiperwall · {layout.Name} v{layout.Version} · {layout.Endpoint}"
        : $"{step.Target?.Name} ({step.Target?.PcName})";
    public static string Details(StepSnapshot step) => step.SavedLayout is { } layout
        ? $"저장 배치 {layout.Name} v{layout.Version} · 항목 {layout.Entries.Length}개\nController: {layout.Endpoint} · 설정 v{layout.ConfigurationVersion}\n" +
          string.Join("\n", layout.Entries.Select(e => $"{e.Name} · {e.Selector}={e.ContentValue} · Zone {e.ZoneId} · 중심 ({e.Bounds.X}, {e.Bounds.Y}) / {e.Bounds.Width}×{e.Bounds.Height}"))
        : $"{Kind(step.Kind)} · {step.Role?.Id} → {Target(step)}\n" +
          $"{step.Operation}={step.Value} {step.Unit} · PC ID {step.Target?.PcId} · 장비 ID {step.Target?.Id}\n" +
          $"모델 {step.Target?.ModelId} · 연결 {step.Target?.ConnectionId} · 실행 v{step.Target?.ExecutionVersion}";
}
