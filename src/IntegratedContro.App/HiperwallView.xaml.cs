using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace IntegratedContro.App;
public partial class HiperwallView : UserControl
{
    private Point _contentDragStart;
    private Window? _previewWindow;
    public HiperwallPreviewCoordinator Previews { get; }
    public HiperwallView()
    {
        InitializeComponent();
        Previews = new(WallCanvas, () => DataContext as HiperwallViewModel);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is HiperwallViewModel old) old.PropertyChanged -= PreviewContextChanged;
            if (e.NewValue is HiperwallViewModel next) next.PropertyChanged += PreviewContextChanged;
            Previews.RefreshContext();
        };
        IsVisibleChanged += (_, _) => UpdatePreviewVisibility();
        WallCanvas.IsVisibleChanged += (_, _) => UpdatePreviewVisibility();
        Loaded += (_, _) =>
        {
            _previewWindow = Window.GetWindow(this);
            if (_previewWindow is not null) _previewWindow.StateChanged += PreviewWindowChanged;
            UpdatePreviewVisibility();
        };
        Unloaded += (_, _) =>
        {
            if (_previewWindow is not null) _previewWindow.StateChanged -= PreviewWindowChanged;
            _previewWindow = null; Previews.SetVisible(false);
        };
        WallCanvas.FindZoneDropTarget = ZoneButtonAt;
        WallCanvas.ZoneDropTargetChanged += HighlightZone;
        WallCanvas.InstanceZoneDropped += async (item, zone) => { if (DataContext is HiperwallViewModel vm) await vm.DropOnZone(item, zone); };
        WallCanvas.GeometryCommitted += async (item, layout) => { if (DataContext is HiperwallViewModel vm) await vm.CommitGeometry(item, layout); };
        DataContextChanged += (_, e) => { if (e.NewValue is HiperwallViewModel vm) vm.ConfirmCloseAll = text =>
            MessageBox.Show(Window.GetWindow(this), text, "Hiperwall 전체 닫기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes; };
    }
    // Scope deletion to the LIVE canvas and read-only instance list. Search, numeric inputs,
    // content library and saved-layout editors keep their normal text/navigation behavior.
    private void RemoveInstanceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || Keyboard.Modifiers != ModifierKeys.None ||
            sender is not UIElement { IsVisible: true, IsKeyboardFocusWithin: true } ||
            DataContext is not HiperwallViewModel vm) return;
        e.Handled = true; // Never let DataGrid delete only its local row.
        if (e.IsRepeat || !vm.CloseInstanceCommand.CanExecute(null)) return;
        WallCanvas.CancelInteraction();
        vm.CloseInstanceCommand.Execute(null);
    }
    private void PreviewContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Previews.RefreshContext();
    private void PreviewWindowChanged(object? sender, EventArgs e) => UpdatePreviewVisibility();
    private void UpdatePreviewVisibility() => Previews.SetVisible(IsLoaded && IsVisible && WallCanvas.IsVisible && _previewWindow?.WindowState != WindowState.Minimized);
    private void ZoomIn(object sender, RoutedEventArgs e) => WallCanvas.Zoom(1.2);
    private void ZoomOut(object sender, RoutedEventArgs e) => WallCanvas.Zoom(1 / 1.2);
    private void FitAll(object sender, RoutedEventArgs e) => WallCanvas.FitAll();
    private void FitSelected(object sender, RoutedEventArgs e) => WallCanvas.FitSelected();
    private void ContentPointerDown(object sender, MouseButtonEventArgs e) => _contentDragStart = e.GetPosition(ContentList);
    private void ContentPointerMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(ContentList) - _contentDragStart).Length < 8 ||
            DataContext is not HiperwallViewModel { CanOperate: true, SelectedContent: { } content }) return;
        DragDrop.DoDragDrop(ContentList, new DataObject(typeof(HiperwallItemRow), content), DragDropEffects.Copy);
    }
    private void CanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataContext is HiperwallViewModel { CanOperate: true } && e.Data.GetDataPresent(typeof(HiperwallItemRow)) &&
            WallCanvas.ZoneAt(e.GetPosition(WallCanvas)) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void CanvasDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not HiperwallViewModel vm || e.Data.GetData(typeof(HiperwallItemRow)) is not HiperwallItemRow content) return;
        var point = e.GetPosition(WallCanvas); var zone = WallCanvas.ZoneAt(point); var world = WallCanvas.ToWorld(point);
        if (zone is not null) await vm.DropContent(content, zone, world.X, world.Y);
    }
}
