using System.Windows;
using System.Windows.Controls;

namespace IntegratedContro.App;

public partial class CameraView : UserControl
{
    private Window? _window;
    public CameraView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is CameraViewModel old) { old.SetVisible(false); old.ClearSecrets = () => { }; }
            if (e.NewValue is CameraViewModel vm)
            {
                vm.ReadRtsp = () => RtspInput.Password; vm.ReadRtspUser = () => RtspUserInput.Password;
                vm.ReadRtspPassword = () => RtspPasswordInput.Password;
                vm.ReadApiPassword = () => ApiPasswordInput.Password; vm.ReadHlsPassword = () => HlsPasswordInput.Password;
                vm.ClearSecrets = () => { RtspInput.Clear(); RtspUserInput.Clear(); RtspPasswordInput.Clear(); ApiPasswordInput.Clear(); HlsPasswordInput.Clear(); };
                vm.ConfirmLocalMediaChange = text => MessageBox.Show(Window.GetWindow(this), text, "로컬 영상 서버 설정",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
                vm.ConfirmForceDelete = text => MessageBox.Show(Window.GetWindow(this), text, "카메라 강제 삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            }
            VisibilityChanged();
        };
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += WindowStateChanged;
            VisibilityChanged();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= WindowStateChanged;
            _window = null; if (DataContext is CameraViewModel vm) vm.SetVisible(false);
        };
        IsVisibleChanged += (_, _) => VisibilityChanged();
    }
    private void WindowStateChanged(object? sender, EventArgs e) => VisibilityChanged();
    private void VisibilityChanged()
    {
        if (DataContext is CameraViewModel vm)
            vm.SetVisible(IsLoaded && IsVisible && (_window ?? Window.GetWindow(this))?.WindowState != WindowState.Minimized);
    }
}
