using System.Windows;
using System.Windows.Input;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class HiperwallCanvas
{
    public static readonly DependencyProperty CanManipulateProperty = DependencyProperty.Register(nameof(CanManipulate), typeof(bool), typeof(HiperwallCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => { if (!(bool)e.NewValue) ((HiperwallCanvas)d).CancelEditGesture(); }));
    public static readonly DependencyProperty LockAspectProperty = DependencyProperty.Register(nameof(LockAspect), typeof(bool), typeof(HiperwallCanvas), new PropertyMetadata(true));
    public static readonly DependencyProperty SnapToGridProperty = DependencyProperty.Register(nameof(SnapToGrid), typeof(bool), typeof(HiperwallCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty TargetZoneProperty = DependencyProperty.Register(nameof(TargetZone), typeof(HiperwallItemRow), typeof(HiperwallCanvas), new PropertyMetadata(null, (d, _) => ((HiperwallCanvas)d).CancelEditGesture()));
    public bool CanManipulate { get => (bool)GetValue(CanManipulateProperty); set => SetValue(CanManipulateProperty, value); }
    public bool LockAspect { get => (bool)GetValue(LockAspectProperty); set => SetValue(LockAspectProperty, value); }
    public bool SnapToGrid { get => (bool)GetValue(SnapToGridProperty); set => SetValue(SnapToGridProperty, value); }
    public HiperwallItemRow? TargetZone { get => (HiperwallItemRow?)GetValue(TargetZoneProperty); set => SetValue(TargetZoneProperty, value); }
    public event Action<HiperwallItemRow, HiperwallLayout>? GeometryCommitted;
    public Func<Point, HiperwallItemRow?>? FindZoneDropTarget { get; set; }
    public event Action<HiperwallItemRow?>? ZoneDropTargetChanged;
    public event Action<HiperwallItemRow, HiperwallItemRow>? InstanceZoneDropped;
    private HiperwallItemRow? _dropZone;
    private void SetDropZone(HiperwallItemRow? zone)
    {
        if (ReferenceEquals(_dropZone, zone)) return;
        _dropZone = zone; ZoneDropTargetChanged?.Invoke(zone);
    }
    private HiperwallItemRow? _dragItem;
    private HiperwallLayout? _dragLayout, _previewLayout;
    private Point _dragStart;
    private double _dragScale;
    private bool _resizing, _dragMoved;
    private TouchDevice? _editTouch;
    public Point ToWorld(Point point)
    {
        EnsureGeometry(); var scale = ViewScale;
        return _bounds.IsEmpty ? new(0, 0) : new((point.X - ActualWidth / 2 - _pan.X) / scale + _bounds.X + _bounds.Width / 2,
            (point.Y - ActualHeight / 2 - _pan.Y) / scale + _bounds.Y + _bounds.Height / 2);
    }
    public HiperwallItemRow? ZoneAt(Point point)
    {
        var world = ToWorld(point);
        return Items?.OfType<HiperwallItemRow>().LastOrDefault(i => i.Kind == HiperwallRowKind.Zone && i.Item.Id is not null &&
            i.TryRectangle(out var r, out _) && world.X >= r.Left && world.X <= r.Right && world.Y >= r.Top && world.Y <= r.Bottom);
    }
    private bool AtResizeHandle(Point point, double radius)
    {
        if (!CanManipulate || Selected?.TryRectangle(out var r, out _) != true) return false;
        var corner = ToScreen(r.Right, r.Bottom); return Math.Abs(point.X - corner.X) <= radius && Math.Abs(point.Y - corner.Y) <= radius;
    }
    private bool SelectForGesture(Point point, double radius)
    {
        Focus();
        if (!AtResizeHandle(point, radius)) SetCurrentValue(SelectedProperty, ItemAt(point));
        return CanManipulate && Selected is { Kind: HiperwallRowKind.Instance } item && item.TryRectangle(out _, out _);
    }
    // Both pointer paths select through the bound property before capturing input. Selection
    // can change CanManipulate/TargetZone, whose callbacks intentionally cancel stale gestures.
    private void BeginEditGesture(Point point)
    {
        if (SelectForGesture(point, 15)) StartSelectedGesture(point, 15);
    }
    private void StartSelectedGesture(Point point, double radius)
    {
        if (!CanManipulate || Selected is not { Kind: HiperwallRowKind.Instance } item || !item.TryRectangle(out var r, out _)) return;
        _dragItem = item; _dragLayout = _previewLayout = HiperwallLayout.From(r); _dragStart = point; _dragScale = ViewScale; _dragMoved = false;
        var corner = ToScreen(r.Right, r.Bottom);
        _resizing = Math.Abs(point.X - corner.X) <= radius && Math.Abs(point.Y - corner.Y) <= radius;
        if (_editTouch is null && !CaptureMouse()) { CancelEditGesture(); return; }
        Cursor = _resizing ? Cursors.SizeNWSE : Cursors.SizeAll;
    }
    private void UpdateEditGesture(Point point)
    {
        if (!CanManipulate || _dragLayout is not { } initial) { CancelEditGesture(); return; }
        var delta = point - _dragStart;
        if (delta.Length < 3 && !_dragMoved) return;
        _dragMoved = true;
        SetDropZone(!_resizing ? FindZoneDropTarget?.Invoke(point) : null);
        if (_dropZone is not null) { _previewLayout = initial; InvalidateVisual(); return; }
        HiperwallRectangle? grid = null; int columns = 0, rows = 0;
        if (SnapToGrid && TargetZone?.TryRectangle(out var zone, out _) == true)
        {
            grid = zone; int.TryParse(TargetZone.Item.Fields.GetValueOrDefault("zonegridh"), out columns);
            int.TryParse(TargetZone.Item.Fields.GetValueOrDefault("zonegridv"), out rows);
        }
        _previewLayout = _resizing ? HiperwallEditing.Resize(initial, delta.X / _dragScale, delta.Y / _dragScale, LockAspect,
            grid is { } g && columns > 0 ? g.Width / columns : 0, grid is { } h && rows > 0 ? h.Height / rows : 0) :
            HiperwallEditing.Move(initial, delta.X / _dragScale, delta.Y / _dragScale, grid, columns, rows);
        InvalidateVisual();
    }
    private void FinishEditGesture()
    {
        var item = _dragItem; var layout = _previewLayout; var zone = _dropZone; var send = _dragMoved && CanManipulate;
        CancelEditGesture();
        if (!send || item is null) return;
        if (zone is not null) InstanceZoneDropped?.Invoke(item, zone);
        else if (layout is { IsValid: true }) GeometryCommitted?.Invoke(item, layout);
    }
    private void CancelEditGesture()
    {
        _dragItem = null; _dragLayout = null; _previewLayout = null; _dragMoved = false; SetDropZone(null);
        if (_editTouch is { } touch) { _editTouch = null; touch.Capture(null); }
        if (IsMouseCaptured && !_panning) ReleaseMouseCapture();
        Cursor = Cursors.Arrow; InvalidateVisual();
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); CancelEditGesture(); }
    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (_editTouch is not null) { CancelEditGesture(); e.Handled = true; return; }
        var point = e.GetTouchPoint(this).Position;
        if (SelectForGesture(point, 22))
        {
            _editTouch = e.TouchDevice;
            if (e.TouchDevice.Capture(this)) StartSelectedGesture(point, 22);
            else CancelEditGesture();
        }
        e.Handled = true;
    }
    protected override void OnTouchMove(TouchEventArgs e)
    { base.OnTouchMove(e); if (_editTouch == e.TouchDevice) UpdateEditGesture(e.GetTouchPoint(this).Position); e.Handled = true; }
    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (_editTouch == e.TouchDevice) { UpdateEditGesture(e.GetTouchPoint(this).Position); FinishEditGesture(); }
        e.Handled = true;
    }
    protected override void OnLostTouchCapture(TouchEventArgs e)
    { base.OnLostTouchCapture(e); if (_editTouch == e.TouchDevice) CancelEditGesture(); }
}
