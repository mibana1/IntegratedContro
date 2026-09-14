using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace IntegratedContro.App;

public sealed partial class HiperwallViewModel
{
    public ObservableCollection<HiperwallItemRow> Instances { get; } = [];
    public ObservableCollection<HiperwallItemRow> CanvasItems { get; } = [];
    public ObservableCollection<string> ContentTypes { get; } = ["모든 유형"];
    public ICollectionView FilteredContents { get; private set; } = null!;
    private string _search = "", _typeFilter = "모든 유형";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) FilterContents(); }
    }
    public string TypeFilter
    {
        get => _typeFilter;
        set { if (Set(ref _typeFilter, value ?? "모든 유형")) FilterContents(); }
    }
    public string ContentCount => $"검색 {FilteredContents.Cast<object>().Count()} / 전체 {Contents.Count}";
    public string InstanceState => ListStatus(_view?.Instances);
    public string InstanceBrief => Brief(_view?.Instances);
    public string ZoneBrief => Brief(_view?.Zones);
    public string WallBrief => Brief(_view?.Walls);
    private static string Brief(Core.HiperwallList? list) => ListStatus(list).Split('\n')[0] +
        (list?.SucceededAt is { } at ? $" · 마지막 성공 {at.ToLocalTime():HH:mm:ss}" : "") +
        (list is { State: Core.HiperwallListState.Unsupported or Core.HiperwallListState.Failed } ? " · " + list.Reason : "");
    public string CanvasSummary => $"좌표 표시 {CanvasItems.Count}개 · 좌표 미제공/해석 제외 {Zones.Count + Instances.Count - CanvasItems.Count}개";
    public string SelectedName => Selected?.Name ?? "항목 선택";
    public string SelectedGeometry => Selected?.Geometry ?? "캔버스나 목록에서 항목을 선택하세요.";
    public HiperwallItemRow? SelectedContent
    {
        get => Selected?.Kind == HiperwallRowKind.Content ? Selected : null;
        set { if (value is not null) Selected = value; }
    }
    public HiperwallItemRow? SelectedZone
    {
        get => Selected?.Kind == HiperwallRowKind.Zone ? Selected : null;
        set { if (value is not null) Selected = value; }
    }
    public HiperwallItemRow? SelectedInstance
    {
        get => Selected?.Kind == HiperwallRowKind.Instance ? Selected : null;
        set { if (value is not null) Selected = value; }
    }
    public HiperwallItemRow? SelectedWall
    {
        get => Selected?.Kind == HiperwallRowKind.Wall ? Selected : null;
        set { if (value is not null) Selected = value; }
    }
    private void InitializeWorkspace()
    {
        FilteredContents = new ListCollectionView(Contents) { Filter = row => row is HiperwallItemRow item && Matches(item) };
        FilteredContents.SortDescriptions.Add(new(nameof(HiperwallItemRow.Name), ListSortDirection.Ascending));
        FilteredContents.SortDescriptions.Add(new(nameof(HiperwallItemRow.Identity), ListSortDirection.Ascending));
        FilteredContents.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HiperwallItemRow.Folder)));
    }
    private bool Matches(HiperwallItemRow item) =>
        (_typeFilter == "모든 유형" || item.Type == _typeFilter) &&
        (string.IsNullOrWhiteSpace(_search) || new[] { item.Name, item.Identity, item.Type }
            .Any(value => value.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)));
    private void FilterContents()
    {
        FilteredContents.Refresh();
        if (Selected is { Kind: HiperwallRowKind.Content } item && !Matches(item)) Selected = null;
        Changed(nameof(ContentCount));
    }
    private void UpdateWorkspace()
    {
        var filter = _typeFilter;
        var types = Contents.Select(x => x.Type).Distinct().Order(StringComparer.Ordinal).ToArray();
        foreach (var old in ContentTypes.Where(t => t != "모든 유형" && !types.Contains(t)).ToArray()) ContentTypes.Remove(old);
        foreach (var type in types) if (!ContentTypes.Contains(type)) ContentTypes.Add(type);
        _typeFilter = ContentTypes.Contains(filter) ? filter : "모든 유형"; Changed(nameof(TypeFilter));
        FilterContents();
        CanvasItems.Clear();
        foreach (var item in Zones.Concat(Instances))
            if (item.TryRectangle(out _, out _)) CanvasItems.Add(item);
        Changed(nameof(CanvasSummary));
    }
}
