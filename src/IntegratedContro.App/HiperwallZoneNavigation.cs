using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace IntegratedContro.App;

public sealed class HiperwallZoneMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [HiperwallItemRow a, HiperwallItemRow b] && a.Item.Id is not null && a.Item.Id == b.Item.Id;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public partial class HiperwallView
{
    public static readonly DependencyProperty IsZoneDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsZoneDropTarget", typeof(bool), typeof(HiperwallView), new PropertyMetadata(false));
    public static bool GetIsZoneDropTarget(DependencyObject element) => (bool)element.GetValue(IsZoneDropTargetProperty);
    public static void SetIsZoneDropTarget(DependencyObject element, bool value) => element.SetValue(IsZoneDropTargetProperty, value);
    private static IEnumerable<Button> ZoneButtons(DependencyObject parent)
    {
        if (parent is Button button) { yield return button; yield break; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in ZoneButtons(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private HiperwallItemRow? ZoneButtonAt(Point canvasPoint)
    {
        // Captured mouse/touch input still belongs to the canvas. Hit test in the sibling strip's DIP space.
        var stripPoint = WallCanvas.TranslatePoint(canvasPoint, ZoneShortcutScroll);
        if (!new Rect(ZoneShortcutScroll.RenderSize).Contains(stripPoint)) return null;
        foreach (var button in ZoneButtons(ZoneShortcuts))
            if (button.IsVisible && button.Tag is HiperwallItemRow zone &&
                new Rect(button.RenderSize).Contains(WallCanvas.TranslatePoint(canvasPoint, button))) return zone;
        return null;
    }
    private void HighlightZone(HiperwallItemRow? zone)
    {
        foreach (var button in ZoneButtons(ZoneShortcuts)) SetIsZoneDropTarget(button, button.IsEnabled &&
            button.Tag is HiperwallItemRow row && row.Item.Id is not null && row.Item.Id == zone?.Item.Id);
    }
    private void ZoneButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HiperwallItemRow zone } && zone.CanNavigateZone &&
            DataContext is HiperwallViewModel vm && vm.Zones.Contains(zone))
        {
            vm.TargetZone = zone; WallCanvas.FitItem(zone);
        }
    }
    private void ZoneButtonDragOver(object sender, DragEventArgs e)
    {
        var item = e.Data.GetData(typeof(HiperwallItemRow)) as HiperwallItemRow;
        var canDrop = item is not null && sender is Button { Tag: HiperwallItemRow zone } &&
            DataContext is HiperwallViewModel vm && vm.CanDropOnZone(item, zone);
        e.Effects = canDrop ? (item!.Kind == HiperwallRowKind.Instance ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        if (sender is Button button) SetIsZoneDropTarget(button, canDrop);
        e.Handled = true;
    }
    private void ZoneButtonDragLeave(object sender, DragEventArgs e)
    { if (sender is Button button) SetIsZoneDropTarget(button, false); e.Handled = true; }
    private async void ZoneButtonDrop(object sender, DragEventArgs e)
    {
        HighlightZone(null); e.Handled = true; e.Effects = DragDropEffects.None;
        if (sender is not Button { Tag: HiperwallItemRow zone } || DataContext is not HiperwallViewModel vm ||
            e.Data.GetData(typeof(HiperwallItemRow)) is not HiperwallItemRow item || !vm.CanDropOnZone(item, zone)) return;
        e.Effects = item.Kind == HiperwallRowKind.Instance ? DragDropEffects.Move : DragDropEffects.Copy;
        await vm.DropOnZone(item, zone);
    }
}
