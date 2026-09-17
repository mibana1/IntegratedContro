using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using IntegratedContro.App;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private sealed class SliderValue : INotifyPropertyChanged
    {
        private double _value = 50;
        public double Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class SliderTouch : TouchDevice, IDisposable
    {
        private readonly UIElement _root;
        private Point _point;
        private TouchAction _action;
        public SliderTouch(UIElement root, int id) : base(id)
        { _root = root; SetActiveSource(PresentationSource.FromVisual(root)); Activate(); }
        public bool Raise(RoutedEvent routedEvent, Point point)
        {
            _point = point;
            _action = routedEvent == UIElement.PreviewTouchDownEvent ? TouchAction.Down :
                routedEvent == UIElement.PreviewTouchUpEvent ? TouchAction.Up : TouchAction.Move;
            var args = new TouchEventArgs(this, Environment.TickCount) { RoutedEvent = routedEvent };
            _root.RaiseEvent(args); return args.Handled;
        }
        public override TouchPoint GetTouchPoint(IInputElement relativeTo)
        {
            var point = relativeTo is UIElement element ? _root.TranslatePoint(_point, element) : _point;
            return new(this, point, new Rect(point, new Size(8, 8)), _action);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => [GetTouchPoint(relativeTo)];
        public void Dispose() { Capture(null); Deactivate(); }
    }
    private static async Task RunDesignControls()
    {
        var primary = new Button { Content = "실행 · 저장", Style = (Style)System.Windows.Application.Current.FindResource("Primary") };
        var secondary = new Button { Content = "상태 확인" };
        var danger = new Button { Content = "선택 삭제", Style = (Style)System.Windows.Application.Current.FindResource("Danger") };
        var choices = new WrapPanel(); choices.Children.Add(primary); choices.Children.Add(secondary); choices.Children.Add(danger);
        var input = new TextBox { Text = "회의실 조명" };
        var password = new PasswordBox { Password = "test-password" };
        var picker = new ComboBox { ItemsSource = new[] { "조명", "프로젝터", "음향" }, SelectedIndex = 0 };
        var checkbox = new CheckBox { Content = "선택한 설정 사용", IsChecked = true };
        var probe = new SliderValue();
        var slider = new TouchSlider { Minimum = 10, Maximum = 90, TickFrequency = 10, Margin = new Thickness(0, 8, 0, 8) };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(SliderValue.Value)) { Source = probe, Mode = BindingMode.TwoWay });
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "공통 조작 · 터치와 키보드", Style = (Style)System.Windows.Application.Current.FindResource("SectionTitle") });
        stack.Children.Add(choices); stack.Children.Add(input); stack.Children.Add(password); stack.Children.Add(picker);
        stack.Children.Add(checkbox); stack.Children.Add(slider);
        stack.Children.Add(new TextBlock { Text = "선택값은 즉시 입력에 반영되며, 실제 전송은 실행 버튼으로 구분합니다.", TextWrapping = TextWrapping.Wrap });
        var window = new Window { Title = "디자인 검증", Width = 680, Height = 670, Content = stack,
            Style = (Style)System.Windows.Application.Current.FindResource("AppWindow") };
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            foreach (var control in new FrameworkElement[] { primary, secondary, danger, input, password, picker, checkbox, slider })
                Require(control.ActualHeight >= 48, $"Small touch target: {control.GetType().Name}, {control.ActualHeight}");
            picker.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var item = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(1);
            Require(item is { ActualHeight: >= 48 }, "Drop-down choices are not touch-sized");
            picker.SelectedIndex = 1; picker.IsDropDownOpen = false; Require((string)picker.SelectedItem == "프로젝터", "Selection failed");
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            Require(track.Thumb.ActualWidth >= 44 && track.Thumb.ActualHeight >= 44, "Slider thumb hit area is too small");
            Point At(double ratio) => new(slider.ActualWidth * ratio, slider.ActualHeight / 2);
            using var touch = new SliderTouch(slider, 301);
            Require(touch.Raise(UIElement.PreviewTouchDownEvent, At(.5)) && touch.Captured == slider && probe.Value == 50,
                "Touch did not capture and set the bound value");
            Require(touch.Raise(UIElement.PreviewTouchMoveEvent, At(-.1)) && probe.Value == 10, "Touch lower clamp failed");
            using (var other = new SliderTouch(slider, 302))
            {
                Require(other.Raise(UIElement.PreviewTouchDownEvent, At(1)) && probe.Value == 10, "Second touch changed the active value");
                other.Raise(UIElement.PreviewTouchUpEvent, At(1));
            }
            var upHandled = touch.Raise(UIElement.PreviewTouchUpEvent, At(1.1));
            Require(upHandled && touch.Captured is null && probe.Value == 90,
                $"Final release failed: handled={upHandled}; captured={touch.Captured}; value={probe.Value}");
            touch.Raise(UIElement.PreviewTouchDownEvent, At(.5)); touch.Capture(null);
            var before = probe.Value;
            Require(!touch.Raise(UIElement.PreviewTouchMoveEvent, At(.9)) && probe.Value == before, "Lost capture continued a gesture");
            touch.Raise(UIElement.PreviewTouchDownEvent, At(.5)); slider.IsEnabled = false;
            Require(touch.Captured is null, "Disabling a slider retained touch capture");
            Require(!touch.Raise(UIElement.PreviewTouchMoveEvent, At(1)) && probe.Value == before, "Disabled slider changed value");
            slider.IsEnabled = true;
            probe.Value = 30; Require(slider.Value == 30, "Touch discarded the two-way binding");
            slider.Focus();
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Require(probe.Value == 40, "Native keyboard/tick navigation was lost");
            slider.FlowDirection = FlowDirection.RightToLeft; window.UpdateLayout();
            touch.Raise(UIElement.PreviewTouchDownEvent, At(0));
            Require(probe.Value == 90, "RTL touch direction failed");
            touch.Raise(UIElement.PreviewTouchUpEvent, At(1)); Require(probe.Value == 10, "RTL release direction failed");
            slider.FlowDirection = FlowDirection.LeftToRight; probe.Value = 60;
            var output = Path.GetFullPath("artifacts/ui-smoke"); Directory.CreateDirectory(output);
            Capture(window, Path.Combine(output, "design-controls.png"));
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Design binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "design-controls-result.txt"),
                "PASS: 48-DIP controls/dropdown rows, 44-DIP slider thumb; synthetic routed WPF touch capture, drag, bounds, second-touch exclusion, release value, capture loss, disabled cleanup, two-way binding, keyboard/ticks and RTL. Physical touchscreen/DPI device verification remains separate.");
            Console.WriteLine("Shared design controls and touch slider WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); window.Close(); }
    }
}
