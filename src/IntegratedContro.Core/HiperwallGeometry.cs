using System.Globalization;

namespace IntegratedContro.Core;

// Geometry is expressed in Controller pixel coordinates, independent of WPF DIPs and viewport zoom.
public readonly record struct HiperwallRectangle(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
}
public static class HiperwallGeometry
{
    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    public static bool TryPair(string text, out double a, out double b)
    {
        a = b = 0;
        var pair = text.Split(',');
        return pair.Length == 2 && TryNumber(pair[0], out a) && TryNumber(pair[1], out b);
    }
    public static bool TryZone(HiperwallItem item, out HiperwallRectangle rect, out string reason)
    {
        rect = default;
        var f = item.Fields;
        if (!Number(f, "left", out var x) || !Number(f, "top", out var y) ||
            !Number(f, "width", out var w) || !Number(f, "height", out var h))
        { reason = "캔버스 제외 · Zone의 left/top/width/height가 모두 필요합니다."; return false; }
        return Rectangle(x, y, w, h, out rect, out reason);
    }
    public static bool TryInstance(HiperwallItem item, out HiperwallRectangle rect, out string reason)
    {
        rect = default;
        var f = item.Fields;
        if (!f.TryGetValue("position", out var position) || !TryPair(position, out var x, out var wireY) ||
            !f.TryGetValue("size", out var size) || !TryPair(size, out var w, out var h))
        { reason = "캔버스 제외 · Instance의 position/size 응답이 필요합니다."; return false; }
        if (f.TryGetValue("rotation", out var rotation) && (!TryNumber(rotation, out var angle) || angle != 0))
        { reason = "캔버스 제외 · 회전된 인스턴스의 표시 좌표 규약은 미확인입니다."; return false; }
        // Reference profile: position is the center, wire Y increases upwards; the app canvas Y increases downwards.
        return Rectangle(x - w / 2, -wireY - h / 2, w, h, out rect, out reason);
    }
    private static bool Number(IReadOnlyDictionary<string, string> f, string key, out double value)
    { value = 0; return f.TryGetValue(key, out var text) && TryNumber(text, out value); }
    private static bool Rectangle(double x, double y, double w, double h, out HiperwallRectangle rect, out string reason)
    {
        rect = default;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
        { reason = "캔버스 제외 · 유효한 위치와 양수 크기가 필요합니다."; return false; }
        // A presentation limit only: the original inventory and raw values remain available.
        if (Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9 || w > 1e9 || h > 1e9 || !double.IsFinite(x + w) || !double.IsFinite(y + h))
        { reason = "캔버스 제외 · 화면 좌표 표시 범위(±10억 px)를 초과합니다. 원문 필드를 확인하세요."; return false; }
        rect = new(x, y, w, h); reason = "Controller 응답 좌표"; return true;
    }
}
