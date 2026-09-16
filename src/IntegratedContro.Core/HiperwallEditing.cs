using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntegratedContro.Core;

public enum HiperwallEditAction { Open, Change, Close, MuteAll, CloseAll, RestoreSlot }
public enum HiperwallSendState { Pending, Sending, Acknowledged, Rejected, Unknown }
public sealed record HiperwallLayout(double X, double Y, double Width, double Height)
{
    public bool IsValid => new[] { X, Y, Width, Height }.All(double.IsFinite) &&
        Math.Abs(X) <= 1e9 && Math.Abs(Y) <= 1e9 && Width is > 0 and <= 1e9 && Height is > 0 and <= 1e9;
    public static HiperwallLayout From(HiperwallRectangle r) => new(r.CenterX, r.CenterY, r.Width, r.Height);
}
public sealed record HiperwallEditRequest(Guid RequestId, long Generation, int ConfigurationVersion, HiperwallEditAction Action,
    string? InstanceId = null, string? Selector = null, string? ContentValue = null, string? ZoneId = null,
    HiperwallLayout? Layout = null, int? Volume = null, bool? Muted = null, string? ExpectedRevision = null)
{
    public int? SlotNumber { get; init; }
    public int? SlotVersion { get; init; }
}
public sealed record HiperwallWireCommand(HiperwallEditAction Action, string? InstanceId = null,
    string? Selector = null, string? ContentValue = null, string? ZoneId = null, HiperwallLayout? Layout = null,
    int? Volume = null, bool? Muted = null);
public sealed record HiperwallWriteResult(HiperwallSendState State, string Message);
public sealed class HiperwallEditStep
{
    public required HiperwallWireCommand Command { get; init; }
    public string? ExpectedTargetRevision { get; init; }
    public HiperwallSendState State { get; set; }
    public string Message { get; set; } = "전송 대기";
}
public sealed class HiperwallEditReceipt
{
    public required HiperwallEditRequest Request { get; init; }
    public required SessionInfo Requester { get; init; }
    public required string Endpoint { get; init; }
    public DateTimeOffset AcceptedAt { get; init; }
    public List<HiperwallEditStep> Steps { get; init; } = [];
    public HiperwallSlot? SlotSnapshot { get; init; }
    public bool Active => Steps.Any(s => s.State is HiperwallSendState.Pending or HiperwallSendState.Sending);
    public bool NeedsAttention => Active || Steps.Any(s => s.State == HiperwallSendState.Unknown);
    public string Summary => $"{HiperwallEditing.ActionName(Request.Action)} · 응답 확인 {Steps.Count(s => s.State == HiperwallSendState.Acknowledged)} / 거부 {Steps.Count(s => s.State == HiperwallSendState.Rejected)} / 결과 확인 필요 {Steps.Count(s => s.State == HiperwallSendState.Unknown)} / 대기·전송 {Steps.Count(s => s.State is HiperwallSendState.Pending or HiperwallSendState.Sending)}";
}
public static class HiperwallEditing
{
    public static string ActionName(HiperwallEditAction action) => action switch
    {
        HiperwallEditAction.Open => "콘텐츠 추가", HiperwallEditAction.Change => "위치·크기·소리 변경",
        HiperwallEditAction.RestoreSlot => "저장 슬롯 불러오기",
        HiperwallEditAction.Close => "선택 닫기", HiperwallEditAction.CloseAll => "전체 닫기", _ => "전체 음소거 변경"
    };
    public static string Revision(IEnumerable<HiperwallItem> items) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(items.OrderBy(i => i.Id, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)
            .Select(i => new { i.Id, i.Name, Fields = i.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() })))));
    public static bool TryAudio(HiperwallItem item, out int volume, out bool muted)
    {
        volume = 0; muted = false;
        var parts = item.Fields.GetValueOrDefault("audio", "").Split(',');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ||
            !double.IsFinite(n) || n is < 0 or > 100 || parts[1].Trim() is not ("muted" or "unmuted")) return false;
        volume = (int)Math.Round(n); muted = parts[1].Trim() == "muted"; return true;
    }
    public static HiperwallLayout Move(HiperwallLayout layout, double dx, double dy, HiperwallRectangle? grid = null, int columns = 0, int rows = 0)
    {
        var x = layout.X + dx; var y = layout.Y + dy;
        if (grid is { } g) { x = Snap(x, columns > 0 ? g.Width / columns : 0, g.Left); y = Snap(y, rows > 0 ? g.Height / rows : 0, g.Top); }
        return layout with { X = x, Y = y };
    }
    public static HiperwallLayout Resize(HiperwallLayout layout, double dw, double dh, bool aspect, double stepX = 0, double stepY = 0)
    {
        var w = Math.Max(16, layout.Width + dw); var h = Math.Max(16, layout.Height + dh);
        var ratio = layout.Width / layout.Height;
        if (aspect) { if (Math.Abs(dh / layout.Height) > Math.Abs(dw / layout.Width)) w = h * ratio; else h = w / ratio; }
        w = Math.Max(16, Snap(w, stepX)); h = Math.Max(16, Snap(h, stepY));
        if (aspect) { w = Math.Max(w, 16 * ratio); h = w / ratio; }
        return new(layout.X + (w - layout.Width) / 2, layout.Y + (h - layout.Height) / 2, w, h);
    }
    private static double Snap(double value, double step, double origin = 0) => step > 0 && double.IsFinite(step)
        ? origin + Math.Round((value - origin) / step, MidpointRounding.AwayFromZero) * step : value;
}
