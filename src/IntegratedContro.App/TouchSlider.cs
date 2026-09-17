using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace IntegratedContro.App;

/// <summary>Shared direct-position slider: one mouse or touch pointer, with native keyboard support.</summary>
public sealed class TouchSlider : Slider
{
    private Track? _track;
    private TouchDevice? _touch;
    private bool _mouse;

    public TouchSlider()
    {
        Unloaded += (_, _) => EndGesture();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) EndGesture(); };
    }

    public override void OnApplyTemplate()
    {
        EndGesture();
        base.OnApplyTemplate();
        _track = GetTemplateChild("PART_Track") as Track;
    }

    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        if (!IsEnabled || _track is null) return;
        e.Handled = true;
        if (_touch is not null || _mouse) return;
        Focus();
        _touch = e.TouchDevice;
        if (!CaptureTouch(_touch)) { _touch = null; return; }
        SetFromPoint(e.GetTouchPoint(_track).Position);
    }
    protected override void OnPreviewTouchMove(TouchEventArgs e)
    {
        if (_touch is null) return;
        e.Handled = true;
        if (_touch == e.TouchDevice && _track is not null && IsEnabled)
            SetFromPoint(e.GetTouchPoint(_track).Position);
    }
    protected override void OnPreviewTouchUp(TouchEventArgs e)
    {
        if (_touch is null) return;
        e.Handled = true;
        if (_touch != e.TouchDevice) return;
        if (_track is not null && IsEnabled) SetFromPoint(e.GetTouchPoint(_track).Position);
        EndGesture();
    }
    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        if (_touch == e.TouchDevice) _touch = null;
        base.OnLostTouchCapture(e);
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || _track is null) return;
        e.Handled = true;
        if (_touch is not null || e.StylusDevice is not null) return;
        Focus();
        _mouse = CaptureMouse();
        if (_mouse) SetFromPoint(e.GetPosition(_track));
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!_mouse || _track is null) return;
        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed) { EndGesture(); return; }
        SetFromPoint(e.GetPosition(_track));
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_mouse) return;
        e.Handled = true;
        if (_track is not null && IsEnabled) SetFromPoint(e.GetPosition(_track));
        EndGesture();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _mouse = false;
        base.OnLostMouseCapture(e);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_touch is not null || _mouse)) { EndGesture(); e.Handled = true; }
        else base.OnPreviewKeyDown(e);
    }
    private void SetFromPoint(Point point)
    {
        if (_track is null || !IsEnabled) return;
        // Track.ValueFromPoint uses the last arranged thumb position. Several pointer
        // events can arrive before layout, so derive an absolute value from track geometry.
        var horizontal = Orientation == Orientation.Horizontal;
        var thumbSize = horizontal ? _track.Thumb.ActualWidth : _track.Thumb.ActualHeight;
        var length = (horizontal ? _track.ActualWidth : _track.ActualHeight) - thumbSize;
        if (length <= 0) return;
        var ratio = Math.Clamp(((horizontal ? point.X : point.Y) - thumbSize / 2) / length, 0, 1);
        if (!horizontal || FlowDirection == FlowDirection.RightToLeft) ratio = 1 - ratio;
        if (IsDirectionReversed) ratio = 1 - ratio;
        var value = Minimum + (Maximum - Minimum) * ratio;
        if (!double.IsFinite(value)) return;
        if (IsSnapToTickEnabled)
        {
            if (Ticks.Count > 0)
                value = Ticks.Append(Minimum).Append(Maximum).MinBy(tick => Math.Abs(tick - value));
            else if (TickFrequency > 0)
                value = Minimum + Math.Round((value - Minimum) / TickFrequency) * TickFrequency;
        }
        SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }
    private void EndGesture()
    {
        var touch = _touch;
        _touch = null; _mouse = false;
        if (touch is not null && touch.Captured == this) ReleaseTouchCapture(touch);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
