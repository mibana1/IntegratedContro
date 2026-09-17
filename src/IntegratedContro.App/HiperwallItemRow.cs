using IntegratedContro.Core;

namespace IntegratedContro.App;

public enum HiperwallRowKind { Wall, Zone, Content, Instance }
public sealed record HiperwallItemRow(HiperwallItem Item, HiperwallRowKind Kind = HiperwallRowKind.Content)
{
    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? "(표시 이름 없음)" : Item.Name;
    public string Identity => Item.Id ?? (Kind == HiperwallRowKind.Content ? "UUID 없음 · 전체 이름으로 추가" : "식별자 없음 · 대상 확정 불가");
    public string KindName => Kind switch { HiperwallRowKind.Zone => "Zone", HiperwallRowKind.Instance => "열린 인스턴스", HiperwallRowKind.Wall => "Wall", _ => "Contents" };
    public string Type => Item.Fields.GetValueOrDefault(Kind == HiperwallRowKind.Instance ? "content.type" : "type", "유형 미제공");
    // Folder grouping is a view of the response name, never a synthetic inventory or identifier.
    public string Folder => Item.Name.Replace('\\', '/').LastIndexOf('/') is var split && split > 0 ?
        Item.Name.Replace('\\', '/')[..split] : "폴더 없는 콘텐츠";
    public bool TryRectangle(out HiperwallRectangle rect, out string reason)
    {
        rect = default; reason = "위치·크기 정보가 있는 Zone 또는 열린 인스턴스를 선택하세요.";
        return Kind == HiperwallRowKind.Zone ? HiperwallGeometry.TryZone(Item, out rect, out reason) :
            Kind == HiperwallRowKind.Instance && HiperwallGeometry.TryInstance(Item, out rect, out reason);
    }
    public string ZoneShortcutLabel => Name == Identity ? Name : $"{Name} · {Identity}";
    public bool CanNavigateZone => Kind == HiperwallRowKind.Zone && Item.Id is not null && TryRectangle(out _, out _);
    public string Geometry => TryRectangle(out var r, out var reason)
        ? FormattableString.Invariant($"중심 X  {r.CenterX:0.###}   /   Y  {r.CenterY:0.###}\n너비  {r.Width:0.###}   /   높이  {r.Height:0.###}\n절대 px · 화면 Y는 아래 방향")
        : reason;
    public string Details => $"{KindName}\n표시 이름: {Name}\n식별자: {Identity}\n출처: 마지막으로 받은 Controller 응답\n\n" +
        string.Join("\n", Item.Fields.Select(p => $"{p.Key}: {p.Value}"));
}
