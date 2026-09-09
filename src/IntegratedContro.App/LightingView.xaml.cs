using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IntegratedContro.App;

public partial class LightingView : UserControl
{
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private MainViewModel? _viewModel;
    private Window? _window;
    private Guid? _source;
    private int _pointerId;
    private Point _origin, _point;
    private bool _dragging;
    private Guid? _targetGroup, _before;
    private TouchDevice? _touch;
    private long _ignoreMouseUntil;

    public LightingView()
    {
        InitializeComponent();
        PreviewMouseDown += HandleMouseDown;
        PreviewMouseMove += HandleMouseMove;
        PreviewMouseUp += HandleMouseUp;
        PreviewTouchDown += HandleTouchDown;
        PreviewTouchMove += HandleTouchMove;
        PreviewTouchUp += HandleTouchUp;
        LostMouseCapture += (_, _) => { if (_source is not null && _touch is null) CancelCardDrag(); };
        LostTouchCapture += (_, e) => { if (_source is not null && _touch == e.TouchDevice) CancelCardDrag(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _source is not null) { CancelCardDrag(); e.Handled = true; } };
        DataContextChanged += (_, _) => BindViewModel();
        Loaded += (_, _) =>
        {
            BindViewModel(); _window = Window.GetWindow(this);
            if (_window is not null) _window.Deactivated += Deactivated;
        };
        Unloaded += (_, _) =>
        {
            CancelCardDrag();
            if (_window is not null) _window.Deactivated -= Deactivated;
            if (_viewModel is not null) _viewModel.PropertyChanged -= ModelChanged;
            _viewModel = null;
        };
        _scrollTimer.Tick += (_, _) => AutoScroll();
    }
    private void Deactivated(object? sender, EventArgs e) => CancelCardDrag();
    private void BindViewModel()
    {
        CancelCardDrag();
        if (_viewModel is not null) _viewModel.PropertyChanged -= ModelChanged;
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_source is not null && _viewModel?.CanArrangeLighting != true) CancelCardDrag();
    }
    private static FrameworkElement? Ancestor(DependencyObject? item, string name)
    {
        while (item is not null)
        {
            if (item is FrameworkElement element && element.Name == name) return element;
            item = item is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(item);
        }
        return null;
    }
    private void HandleMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Environment.TickCount64 < _ignoreMouseUntil || _touch is not null) { if (_viewModel?.EditingLightOrder == true) e.Handled = true; return; }
        if (Ancestor(e.OriginalSource as DependencyObject, "CardContainer")?.DataContext is not LightCard card) return;
        if (!BeginCardDrag(card.Id, e.GetPosition(this), -1)) return;
        e.Handled = true;
        if (!CaptureMouse()) { CancelCardDrag(); return; }
        Focus();
    }
    private void HandleMouseMove(object? sender, MouseEventArgs e)
    {
        if (_source is null || _pointerId != -1) return;
        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelCardDrag(); return; }
        MoveCardDrag(e.GetPosition(this), -1);
    }
    private void HandleMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_source is null || _pointerId != -1) return;
        e.Handled = true; EndCardDrag(e.GetPosition(this), -1);
    }
    private void HandleTouchDown(object? sender, TouchEventArgs e)
    {
        if (_source is not null) { if (_viewModel?.EditingLightOrder == true) e.Handled = true; return; }
        if (Ancestor(e.OriginalSource as DependencyObject, "CardContainer")?.DataContext is not LightCard card) return;
        if (!BeginCardDrag(card.Id, e.GetTouchPoint(this).Position, e.TouchDevice.Id)) return;
        e.Handled = true; _ignoreMouseUntil = Environment.TickCount64 + 1000;
        _touch = e.TouchDevice;
        if (!_touch.Capture(this)) { CancelCardDrag(); return; }
        Focus();
    }
    private void HandleTouchMove(object? sender, TouchEventArgs e)
    {
        if (_source is null || e.TouchDevice.Id != _pointerId) return;
        e.Handled = true; MoveCardDrag(e.GetTouchPoint(this).Position, e.TouchDevice.Id);
    }
    private void HandleTouchUp(object? sender, TouchEventArgs e)
    {
        if (_source is null || e.TouchDevice.Id != _pointerId) return;
        e.Handled = true; _ignoreMouseUntil = Environment.TickCount64 + 1000;
        EndCardDrag(e.GetTouchPoint(this).Position, e.TouchDevice.Id);
    }

    // Mouse and touch share this captured-pointer pipeline. It edits only the local layout draft.
    public bool BeginCardDrag(Guid deviceId, Point point, int pointerId)
    {
        if (_source is not null || _viewModel?.CanArrangeLighting != true ||
            !_viewModel.Lights.Any(c => c.Id == deviceId)) return false;
        _source = deviceId; _pointerId = pointerId; _origin = _point = point; _dragging = false;
        return true;
    }
    public void MoveCardDrag(Point point, int pointerId)
    {
        if (_source is null || pointerId != _pointerId) return;
        if (_viewModel?.CanArrangeLighting != true) { CancelCardDrag(); return; }
        _point = point;
        if (!_dragging && (point - _origin).Length < 8) return;
        _dragging = true; Cursor = Cursors.SizeAll;
        _viewModel.Lights.Single(c => c.Id == _source).IsDragging = true;
        UpdateDrop(); _scrollTimer.Start();
    }
    public bool EndCardDrag(Point point, int pointerId)
    {
        if (_source is null || pointerId != _pointerId) return false;
        MoveCardDrag(point, pointerId);
        var source = _source; var target = _targetGroup; var before = _before;
        var commit = _dragging && _viewModel?.CanArrangeLighting == true && source is not null && target is not null;
        CancelCardDrag(); // Release capture and swallow the release before any collection/layout changes.
        return commit && _viewModel!.MoveLightToGroup(source!.Value, target!.Value, before);
    }
    public void CancelCardDrag()
    {
        _source = null; _dragging = false; _targetGroup = null; _before = null; _scrollTimer.Stop();
        var touch = _touch; _touch = null;
        if (touch is not null) { _ignoreMouseUntil = Environment.TickCount64 + 1000; touch.Capture(null); }
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = null;
        if (_viewModel is not null)
        {
            foreach (var card in _viewModel.Lights) { card.IsDragging = false; card.DropEdge = ""; }
            foreach (var group in _viewModel.LightGroups) group.IsDropTarget = false;
        }
    }
    private void UpdateDrop()
    {
        if (_viewModel is null) return;
        foreach (var card in _viewModel.Lights) card.DropEdge = "";
        foreach (var group in _viewModel.LightGroups) group.IsDropTarget = false;
        _targetGroup = null; _before = null;
        var scrollPoint = TranslatePoint(_point, BoardScroll);
        if (scrollPoint.X < 0 || scrollPoint.Y < 0 || scrollPoint.X >= BoardScroll.ActualWidth || scrollPoint.Y >= BoardScroll.ActualHeight) return;
        var hit = InputHitTest(_point) as DependencyObject;
        if (Ancestor(hit, "GroupDropZone")?.DataContext is not LightGroupRow target) return;
        _targetGroup = target.Id;
        if (Ancestor(hit, "CardContainer") is { DataContext: LightCard over } tile)
        {
            var local = TranslatePoint(_point, tile);
            var before = local.X < tile.ActualWidth / 2;
            var index = target.Cards.IndexOf(over) + (before ? 0 : 1);
            _before = index < target.Cards.Count ? target.Cards[index].Id : null;
            over.DropEdge = before ? "before" : "after";
        }
        else target.IsDropTarget = true; // Empty group or background: append.
    }
    private void AutoScroll()
    {
        if (!_dragging || _viewModel?.CanArrangeLighting != true) { CancelCardDrag(); return; }
        var point = TranslatePoint(_point, BoardScroll);
        if (point.X < 0 || point.X > BoardScroll.ActualWidth) return;
        var delta = point.Y < 30 ? -14 : point.Y > BoardScroll.ActualHeight - 30 ? 14 : 0;
        if (delta == 0) return;
        BoardScroll.ScrollToVerticalOffset(BoardScroll.VerticalOffset + delta);
        BoardScroll.UpdateLayout(); UpdateDrop();
    }
}
