using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwallZoneShortcuts(MainWindow window, MainViewModel vm,
        IntegratedContro.App.HiperwallView view, HiperwallCanvas canvas, HiperwallEditorFixture fixture, string instanceId)
    {
        var h = vm.Hiperwall;
        Button ZoneButton(string id)
        {
            window.UpdateLayout();
            return FindAll<Button>((ItemsControl)view.FindName("ZoneShortcuts")).Single(b => b.Tag is HiperwallItemRow row && row.Item.Id == id);
        }
        HiperwallItemRow Instance() => h.Instances.Single(i => i.Item.Id == instanceId);
        HiperwallRectangle Geometry()
        { Require(Instance().TryRectangle(out var rect, out _), "No source geometry"); return rect; }
        void MouseGesture(string method, params object[] values) => typeof(HiperwallCanvas)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, values);
        DragEventArgs DropArgs(Button button, HiperwallItemRow source, RoutedEvent routedEvent)
        {
            // WPF creates these internally from OLE input. Exercise the production routed handlers with an in-process data object.
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [new DataObject(typeof(HiperwallItemRow), source), DragDropKeyStates.LeftMouseButton,
                    DragDropEffects.Copy | DragDropEffects.Move, button, new Point(10, 10)], null)!;
            args.RoutedEvent = routedEvent; return args;
        }
        Point ButtonPoint(string id)
        {
            var button = ZoneButton(id); return button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), canvas);
        }
        void ExpectZone(string zoneId, HiperwallRectangle before)
        {
            var after = Geometry(); var zone = h.Zones.Single(z => z.Item.Id == zoneId);
            Require(zone.TryRectangle(out var target, out _), "Zone geometry missing");
            Require(after.Width == before.Width && after.Height == before.Height && after.CenterX == target.CenterX && after.CenterY == target.CenterY,
                "Zone drop changed dimensions or missed center");
            var command = fixture.Commands.Last();
            Require(command.Attribute("type")?.Value == "change" && command.Element("id")?.Value == instanceId &&
                command.Element("zone")?.Value == zoneId && command.Element("volume") is null && command.Element("mute") is null,
                "Zone move changed wrong instance/Zone/audio");
        }
        int commands = fixture.Commands.Count;
        h.SelectedInstance = Instance(); var selected = h.Selected;
        ZoneButton("zone-1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var zone1 = h.Zones.Single(z => z.Item.Id == "zone-1"); zone1.TryRectangle(out var z1, out _);
        var center = canvas.ToWorld(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
        Require(h.TargetZone == zone1 && ReferenceEquals(h.Selected, selected) && Math.Abs(center.X - z1.CenterX) < .001 &&
            Math.Abs(center.Y - z1.CenterY) < .001 && fixture.Commands.Count == commands, "Zone click did not navigate only");
        Require(ZoneButton("zone-1").IsEnabled && ZoneButton("zone-2").IsEnabled, "Zone buttons missing");
        // Move a source larger than the destination. Inspector drafts must never become the moved dimensions.
        h.EditWidth = "2560.25"; h.EditHeight = "1440.5";
        h.ApplyGeometryCommand.Execute(null); await Wait(() => !h.IsBusy); commands = fixture.Commands.Count;
        var before = Geometry(); h.EditWidth = "123"; h.EditHeight = "456";
        canvas.FitSelected(); window.UpdateLayout();
        var start = canvas.ToScreen(before.CenterX, before.CenterY); var destination = ButtonPoint("zone-2");
        Require(canvas.FindZoneDropTarget!(destination)?.Item.Id == "zone-2", "Zone button not reachable from canvas coordinates");
        MouseGesture("BeginEditGesture", start); MouseGesture("UpdateEditGesture", destination);
        Require(fixture.Commands.Count == commands && IntegratedContro.App.HiperwallView.GetIsZoneDropTarget(ZoneButton("zone-2")), "Zone drop did not highlight or sent before release");
        MouseGesture("FinishEditGesture"); await Wait(() => !h.IsBusy);
        Require(fixture.Commands.Count == ++commands, "Mouse Zone drop sent more than one command"); ExpectZone("zone-2", before);
        h.SelectedInstance = Instance(); canvas.FitSelected(); window.UpdateLayout();
        before = Geometry(); start = canvas.ToScreen(before.CenterX, before.CenterY); destination = ButtonPoint("zone-1");
        using (var touch = new EditorTouch(canvas, 1201, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start); touch.Raise(UIElement.TouchMoveEvent, destination);
            Require(fixture.Commands.Count == commands, "Touch Zone drop sent before release");
            touch.Raise(UIElement.TouchUpEvent, destination);
        }
        await Wait(() => !h.IsBusy);
        Require(fixture.Commands.Count == ++commands, "Touch Zone drop sent more than one command"); ExpectZone("zone-1", before);
        Require(!IntegratedContro.App.HiperwallView.GetIsZoneDropTarget(ZoneButton("zone-1")), "Drop feedback retained after completion");
        // Library source uses the routed drop handler and native dimensions; no shrink to fit.
        var source = h.Contents.Single(c => c.Item.Id == "source-1"); var count = h.Instances.Count;
        var button2 = ZoneButton("zone-2");
        var over = DropArgs(button2, source, DragDrop.DragOverEvent); button2.RaiseEvent(over);
        Require(over.Handled && over.Effects == DragDropEffects.Copy && IntegratedContro.App.HiperwallView.GetIsZoneDropTarget(button2), "Contents drag-over not accepted");
        var drop = DropArgs(button2, source, DragDrop.DropEvent); button2.RaiseEvent(drop); await Wait(() => !h.IsBusy);
        Require(drop.Handled && fixture.Commands.Count == ++commands && h.Instances.Count == count + 1 && h.SelectedInstance is { } added &&
            added.TryRectangle(out var addedRect, out _) && addedRect.Width == 2560 && addedRect.Height == 1440 &&
            added.Item.Fields.GetValueOrDefault("content.zone") == "zone-2", "Contents Zone drop lost native size or destination");
        // A stale source object cannot be redirected to a refreshed instance with the same ID.
        await h.DropOnZone(selected!, h.Zones[0]); Require(commands == fixture.Commands.Count, "Stale source accepted");
        h.SelectedInstance = Instance(); canvas.FitSelected(); window.UpdateLayout();
        before = Geometry(); start = canvas.ToScreen(before.CenterX, before.CenterY); destination = ButtonPoint("zone-2");
        using (var touch = new EditorTouch(canvas, 1202, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start); touch.Raise(UIElement.TouchMoveEvent, destination);
            touch.Capture(null); touch.Raise(UIElement.TouchUpEvent, destination);
        }
        Require(commands == fixture.Commands.Count, "Cancelled Zone drop sent a command");
        await Execute(vm, vm.ReleaseCommand);
        button2 = ZoneButton("zone-2"); button2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(button2.IsEnabled && h.TargetZone?.Item.Id == "zone-2" && fixture.Commands.Count == commands, "Read-only Zone navigation failed");
        drop = DropArgs(button2, h.Contents[0], DragDrop.DropEvent); button2.RaiseEvent(drop);
        Require(drop.Effects == DragDropEffects.None && commands == fixture.Commands.Count, "Drop sent without lease");
        await Execute(vm, vm.AcquireCommand); h.SelectedInstance = Instance();
        var result = "PASS: Zone buttons render with distinct IDs; click navigates without commands or changed source selection; mouse/touch canvas-to-button drops preserve exact pre-drag size (including larger-than-Zone sources and decimal sizes), center on the requested Zone and send once; library routed drop opens at native size; stale/cancelled/lease-free drops blocked. Isolated HTTPS host and fake Controller; synthetic WPF input, not physical mouse/touchscreen/Controller.";
        await File.WriteAllTextAsync(Path.Combine(FindRepositoryRootForZones(), "artifacts", "ui-smoke", "hiperwall-zone-shortcut-result.txt"), result);
        Console.WriteLine(result);
    }
    private static string FindRepositoryRootForZones()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IntegratedContro.sln"))) root = root.Parent;
        return root!.FullName;
    }
}
