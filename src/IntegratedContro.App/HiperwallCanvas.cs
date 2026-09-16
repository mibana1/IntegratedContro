using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IntegratedContro.App;

// Viewport and edit gestures are local; committed geometry is sent through the ViewModel and host.
public sealed partial class HiperwallCanvas : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(HiperwallCanvas),
        new FrameworkPropertyMetadata(null, ItemsChanged));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(HiperwallItemRow), typeof(HiperwallCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HiperwallCanvas)d).CancelEditGesture()));
    private static readonly DependencyPropertyKey ZoomLabelPropertyKey = DependencyProperty.RegisterReadOnly(nameof(ZoomLabel), typeof(string),
        typeof(HiperwallCanvas), new PropertyMetadata("전체 맞춤"));
    public static readonly DependencyProperty ZoomLabelProperty = ZoomLabelPropertyKey.DependencyProperty;
    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public HiperwallItemRow? Selected { get => (HiperwallItemRow?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public string ZoomLabel => (string)GetValue(ZoomLabelProperty);
    public double ZoomFactor { get; private set; } = 1;
    public double ViewScale { get { EnsureGeometry(); return FitScale * ZoomFactor; } }
    private readonly List<(HiperwallItemRow Item, Rect Rect)> _shapes = [];
    private Rect _bounds = Rect.Empty;
    private double? _retainedScale;
    private Point? _retainedCenter;
    private bool _dirty = true, _panning;
    private Vector _pan;
    private Point _lastPointer;
    private double FitScale => _bounds.IsEmpty ? 1 :
        Math.Min(Math.Max(1, ActualWidth - Math.Min(72, ActualWidth * .15)) / Math.Max(1, _bounds.Width), Math.Max(1, ActualHeight - Math.Min(72, ActualHeight * .15)) / Math.Max(1, _bounds.Height));
    public HiperwallCanvas()
    {
        Focusable = true; ClipToBounds = true;
        Stylus.SetIsPressAndHoldEnabled(this, false); Stylus.SetIsFlicksEnabled(this, false);
        SizeChanged += (_, _) => InvalidateVisual();
        Unloaded += (_, _) => { EndPan(); CancelEditGesture(); };
    }
    private static void ItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (HiperwallCanvas)d;
        if (e.OldValue is INotifyCollectionChanged old) CollectionChangedEventManager.RemoveHandler(old, canvas.OnItemsChanged);
        if (e.NewValue is INotifyCollectionChanged next) CollectionChangedEventManager.AddHandler(next, canvas.OnItemsChanged);
        canvas.ResetGeometry();
    }
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ResetGeometry();
    private void ResetGeometry()
    {
        if (!_dirty && !_bounds.IsEmpty && _retainedScale is null) { _retainedScale = ViewScale; _retainedCenter = ToWorld(new(ActualWidth / 2, ActualHeight / 2)); }
        _dirty = true; _shapes.Clear(); CancelEditGesture(); InvalidateVisual();
    }
    private void EnsureGeometry()
    {
        if (!_dirty) return;
        _dirty = false; _shapes.Clear(); _bounds = Rect.Empty;
        if (Items is null) return;
        foreach (var item in Items.OfType<HiperwallItemRow>())
        {
            if (!item.TryRectangle(out var rect, out _)) continue;
            var box = new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
            _shapes.Add((item, box)); _bounds.Union(box);
        }
        if (!_bounds.IsEmpty && _retainedScale is { } saved && _retainedCenter is { } center)
        {
            ZoomFactor = Math.Clamp(saved / FitScale, .05, 20);
            _pan = new Vector((_bounds.X + _bounds.Width / 2 - center.X) * ViewScale, (_bounds.Y + _bounds.Height / 2 - center.Y) * ViewScale);
            _retainedScale = null; _retainedCenter = null;
        }
        // Remote layer only affects presentation order; an absent layer is not written back as zero.
        _shapes.Sort((a, b) =>
        {
            var kind = (a.Item.Kind == HiperwallRowKind.Instance).CompareTo(b.Item.Kind == HiperwallRowKind.Instance);
            if (kind != 0) return kind;
            return Layer(a.Item).CompareTo(Layer(b.Item));
        });
    }
    private static double Layer(HiperwallItemRow item) =>
        item.Item.Fields.TryGetValue("layer", out var text) && Core.HiperwallGeometry.TryNumber(text, out var layer) ? layer : 0;
    public Point ToScreen(double x, double y)
    {
        EnsureGeometry();
        if (_bounds.IsEmpty) return new(ActualWidth / 2, ActualHeight / 2);
        var scale = FitScale * ZoomFactor;
        return new((x - _bounds.X - _bounds.Width / 2) * scale + ActualWidth / 2 + _pan.X,
            (y - _bounds.Y - _bounds.Height / 2) * scale + ActualHeight / 2 + _pan.Y);
    }
    public HiperwallItemRow? ItemAt(Point point)
    {
        EnsureGeometry();
        if (_bounds.IsEmpty) return null;
        var scale = FitScale * ZoomFactor;
        var world = new Point((point.X - ActualWidth / 2 - _pan.X) / scale + _bounds.X + _bounds.Width / 2,
            (point.Y - ActualHeight / 2 - _pan.Y) / scale + _bounds.Y + _bounds.Height / 2);
        return _shapes.LastOrDefault(shape => shape.Rect.Contains(world)).Item;
    }
    public void FitAll() { EnsureGeometry(); ZoomFactor = 1; _pan = default; InvalidateVisual(); }
    public void FitSelected() { if (Selected is { } item) FitItem(item); }
    public void FitItem(HiperwallItemRow item)
    {
        EnsureGeometry();
        if (!item.TryRectangle(out var r, out _)) return;
        CancelEditGesture();
        ZoomFactor = Math.Clamp(Math.Min(Math.Max(1, ActualWidth - Math.Min(96, ActualWidth * .2)) / Math.Max(1, r.Width),
            Math.Max(1, ActualHeight - Math.Min(96, ActualHeight * .2)) / Math.Max(1, r.Height)) / FitScale, 0.05, 20);
        _pan = default;
        var center = ToScreen(r.CenterX, r.CenterY);
        _pan = new Vector(ActualWidth / 2 - center.X, ActualHeight / 2 - center.Y);
        InvalidateVisual();
    }
    public void Zoom(double multiplier) => ZoomAt(multiplier, new(ActualWidth / 2, ActualHeight / 2));
    private void ZoomAt(double multiplier, Point pivot)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0) return;
        var before = ZoomFactor;
        ZoomFactor = Math.Clamp(ZoomFactor * multiplier, 0.05, 20);
        var ratio = ZoomFactor / before;
        _pan = new Vector(pivot.X - ActualWidth / 2 - (pivot.X - ActualWidth / 2 - _pan.X) * ratio,
            pivot.Y - ActualHeight / 2 - (pivot.Y - ActualHeight / 2 - _pan.Y) * ratio);
        InvalidateVisual();
    }
    public Func<HiperwallItemRow, WallPreview?>? Preview { get; set; }
    public HiperwallItemRow[] VisibleInstances()
    {
        EnsureGeometry();
        return _shapes.Where(s => s.Item.Kind == HiperwallRowKind.Instance &&
            new Rect(ToScreen(s.Rect.Left, s.Rect.Top), new Size(Math.Max(.5, s.Rect.Width * ViewScale),
                Math.Max(.5, s.Rect.Height * ViewScale))).IntersectsWith(new Rect(RenderSize))).Select(s => s.Item).ToArray();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); EnsureGeometry();
        dc.DrawRectangle(Brush("#080F18"), null, new Rect(RenderSize));
        var grid = new Pen(Brush("#152333"), 1);
        for (double x = 0; x < ActualWidth; x += 32) dc.DrawLine(grid, new(x, 0), new(x, ActualHeight));
        for (double y = 0; y < ActualHeight; y += 32) dc.DrawLine(grid, new(0, y), new(ActualWidth, y));
        if (_bounds.IsEmpty)
        {
            DrawText(dc, "표시할 좌표가 없습니다", new(24, 32), 17, "#D3DFEC", Math.Max(1, ActualWidth - 48));
            DrawText(dc, "연결·조회 상태와 좌표 미제공 사유를 확인하세요.", new(24, 66), 12, "#8C9FB5", Math.Max(1, ActualWidth - 48));
            SetValue(ZoomLabelPropertyKey, "전체 맞춤");
            return;
        }
        var scale = FitScale * ZoomFactor;
        SetValue(ZoomLabelPropertyKey, $"{scale * 100:0.##}%");
        foreach (var (item, world) in _shapes)
        {
            var box = _dragItem == item && _previewLayout is { } preview
                ? new Rect(preview.X - preview.Width / 2, preview.Y - preview.Height / 2, preview.Width, preview.Height) : world;
            var start = ToScreen(box.Left, box.Top);
            var rect = new Rect(start.X, start.Y, Math.Max(0.5, box.Width * scale), Math.Max(0.5, box.Height * scale));
            if (!rect.IntersectsWith(new Rect(RenderSize))) continue;
            var instance = item.Kind == HiperwallRowKind.Instance;
            var selected = ReferenceEquals(item, Selected);
            dc.DrawRectangle(Brush(instance ? "#C02B4668" : "#482D8075"),
                new Pen(Brush(selected ? "#FFD58A" : instance ? "#7EBCFF" : "#48BDA7"), selected ? 3 : 1.5), rect);
            dc.PushClip(new RectangleGeometry(rect));
            var frame = instance ? Preview?.Invoke(item) : null;
            if (frame?.Image is { } image)
            {
                dc.DrawImage(image, rect);
                dc.DrawRectangle(Brush("#B0080F18"), null, new Rect(rect.Left, rect.Top, rect.Width, Math.Min(54, rect.Height * .45)));
            }
            if (instance && frame?.Failed == true && rect.Width > 65 && rect.Height > 75)
                DrawText(dc, frame.Message, new(rect.Left + 8, rect.Bottom - 23), 11, "#FFD58A", rect.Width - 16);
            if (rect.Width > 45 && rect.Height > 30)
            {
                DrawText(dc, item.Name, new(rect.Left + 10, rect.Top + 8), 13, "#F1F6FC", rect.Width - 20);
                if (rect.Height > 65)
                    DrawText(dc, $"{item.KindName} · {item.Identity}", new(rect.Left + 10, rect.Top + 31), 10, "#B7CBDC", rect.Width - 20);
            }
            dc.Pop();
            if (frame?.Failed == true)
            {
                dc.DrawRectangle(null, new Pen(Brush("#FFD58A"), 2), rect);
                if (rect.Width >= 18 && rect.Height >= 18)
                {
                    var badge = new Rect(rect.Right - 18, rect.Top, 18, 18);
                    dc.DrawRectangle(Brush("#FFD58A"), null, badge);
                    DrawText(dc, "!", new(badge.Left + 5, badge.Top), 13, "#080F18", 12);
                }
            }
            if (selected && instance && CanManipulate)
                dc.DrawRectangle(Brush("#FFD58A"), new Pen(Brush("#152333"), 1), new Rect(rect.Right - 10, rect.Bottom - 10, 20, 20));
        }
        if (IsKeyboardFocused) dc.DrawRectangle(null, new Pen(Brush("#7EBCFF"), 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
    }
    private void DrawText(DrawingContext dc, string value, Point at, double size, string color, double maxWidth)
    {
        var text = new FormattedText(value.Length > 100 ? value[..100] + "…" : value, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, at);
    }
    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsTouchMouse(e)) { e.Handled = true; return; }
        Focus();
        if (e.ChangedButton == MouseButton.Left) BeginEditGesture(e.GetPosition(this));
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            CancelEditGesture(); _panning = true; _lastPointer = e.GetPosition(this); CaptureMouse(); Cursor = Cursors.Hand;
        }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsTouchMouse(e)) { e.Handled = true; return; }
        if (_dragItem is not null) { UpdateEditGesture(e.GetPosition(this)); e.Handled = true; return; }
        if (!_panning) return;
        var at = e.GetPosition(this); _pan += at - _lastPointer; _lastPointer = at; InvalidateVisual(); e.Handled = true;
    }
    private bool IsTouchMouse(MouseEventArgs e) => _editTouch is not null || e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch;
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (IsTouchMouse(e)) { e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left && _dragItem is not null)
        { UpdateEditGesture(e.GetPosition(this)); FinishEditGesture(); }
        EndPan(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e); _panning = false;
        // A promoted mouse capture change must not discard the active touch gesture.
        if (_editTouch is null) { CancelEditGesture(); Cursor = Cursors.Arrow; }
    }
    private void EndPan() { _panning = false; if (IsMouseCaptured) ReleaseMouseCapture(); Cursor = Cursors.Arrow; }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    { base.OnMouseWheel(e); ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: Zoom(1.2); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.2); break;
            case Key.F: FitAll(); break;
            case Key.Escape: CancelEditGesture(); SetCurrentValue(SelectedProperty, null); break;
            default: return;
        }
        e.Handled = true;
    }
}
