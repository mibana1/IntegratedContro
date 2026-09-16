using System.Reflection;
using System.Windows;
using System.Windows.Input;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwallPointers(MainWindow window, MainViewModel vm, HiperwallCanvas canvas,
        HiperwallEditorFixture fixture, string openedId)
    {
        var h = vm.Hiperwall;
        // WPF's MouseEventArgs reads the OS pointer position. Exercise the production mouse
        // entry pipeline with explicit DIP coordinates; do not claim native mouse injection.
        void MouseGesture(string method, params object[] values) => typeof(HiperwallCanvas)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, values);
        HiperwallRectangle Rect(string id)
        {
            var row = h.Instances.Single(i => i.Item.Id == id);
            Require(row.TryRectangle(out var r, out _), "Instance geometry missing: " + id); return r;
        }
        int commands = fixture.Commands.Count;
        var external = Rect("external-1");
        h.TargetZone = null; h.SelectedContent = h.Contents[0]; window.UpdateLayout();
        var start = canvas.ToScreen(external.CenterX, external.CenterY);
        using (var touch = new EditorTouch(canvas, 1100, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start);
            Require(h.CanMove && canvas.CanManipulate && h.TargetZone?.Item.Id == "zone-1", "Existing source Zone was not inferred");
            touch.Raise(UIElement.TouchUpEvent, start);
        }
        Require(commands == fixture.Commands.Count, "Selecting an existing source sent a command");
        h.TargetZone = h.Zones[0]; h.SelectedContent = h.Contents[0]; window.UpdateLayout();
        var scale = canvas.ViewScale;
        MouseGesture("BeginEditGesture", start);
        Require(h.SelectedInstance?.Item.Id == "external-1" && h.CanMove && canvas.IsMouseCaptured, "First mouse press did not select/capture zone-less instance");
        MouseGesture("UpdateEditGesture", start + new Vector(25, 12));
        Require(commands == fixture.Commands.Count, "Mouse move sent before release");
        MouseGesture("FinishEditGesture"); await Wait(() => !h.IsBusy);
        var dragged = Rect("external-1");
        Require(fixture.Commands.Count == ++commands && Math.Abs(dragged.CenterX - external.CenterX - 25 / scale) < .001 &&
            Math.Abs(dragged.CenterY - external.CenterY - 12 / scale) < .001, "Mouse first-drag coordinate/command count failed");
        window.UpdateLayout();
        var corner = canvas.ToScreen(dragged.Right, dragged.Bottom);
        MouseGesture("BeginEditGesture", corner); MouseGesture("UpdateEditGesture", corner + new Vector(20, 11.25)); MouseGesture("FinishEditGesture");
        await Wait(() => !h.IsBusy);
        var resized = Rect("external-1");
        Require(fixture.Commands.Count == ++commands && resized.Width > dragged.Width && Math.Abs(resized.Width / resized.Height - 16.0 / 9) < .0001,
            "Mouse resize failed");
        h.SelectedInstance = h.Instances.Single(i => i.Item.Id == openedId);
        canvas.FitSelected(); canvas.Zoom(.8); window.UpdateLayout();
        var before = Rect(openedId); start = canvas.ToScreen(before.CenterX, before.CenterY); scale = canvas.ViewScale;
        h.SelectedContent = h.Contents[0]; window.UpdateLayout();
        using (var touch = new EditorTouch(canvas, 1101, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start);
            Require(ReferenceEquals(canvas.Selected, h.SelectedInstance) && h.SelectedInstance?.Item.Id == openedId &&
                ReferenceEquals(touch.Captured, canvas), "Touch did not select/capture new instance");
            // Touch promotion can change mouse capture; it must not cancel the touch contact.
            canvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            touch.Raise(UIElement.TouchMoveEvent, start + new Vector(18, 8));
            Require(commands == fixture.Commands.Count && ReferenceEquals(touch.Captured, canvas), "Touch sent early or lost capture");
            touch.Raise(UIElement.TouchUpEvent, start + new Vector(25, 12));
            Require(touch.Captured is null, "Touch up retained capture");
        }
        await Wait(() => !h.IsBusy); dragged = Rect(openedId);
        Require(fixture.Commands.Count == ++commands && Math.Abs(dragged.CenterX - before.CenterX - 25 / scale) < .001 &&
            Math.Abs(dragged.CenterY - before.CenterY - 12 / scale) < .001, "Touch final release geometry failed after zoom");
        window.UpdateLayout(); corner = canvas.ToScreen(dragged.Right, dragged.Bottom);
        var handle = corner + new Vector(18, 18); // Beyond the old 15-DIP radius, still inside the touch target.
        using (var touch = new EditorTouch(canvas, 1102, handle))
        {
            touch.Raise(UIElement.TouchDownEvent, handle);
            Require(ReferenceEquals(touch.Captured, canvas) && h.SelectedInstance?.Item.Id == openedId, "Touch resize handle selected background");
            touch.Raise(UIElement.TouchMoveEvent, handle + new Vector(20, 11.25));
            touch.Raise(UIElement.TouchUpEvent, handle + new Vector(20, 11.25));
        }
        await Wait(() => !h.IsBusy); resized = Rect(openedId);
        Require(fixture.Commands.Count == ++commands && resized.Width > dragged.Width && Math.Abs(resized.Width / resized.Height - 16.0 / 9) < .0001 &&
            Math.Abs(resized.Left - dragged.Left) < .001 && Math.Abs(resized.Top - dragged.Top) < .001, "Touch resize did not anchor top left/preserve aspect");
        window.UpdateLayout(); start = canvas.ToScreen(resized.CenterX, resized.CenterY);
        using (var touch = new EditorTouch(canvas, 1103, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start);
            touch.Raise(UIElement.TouchMoveEvent, start + new Vector(1, 1));
            touch.Raise(UIElement.TouchUpEvent, start + new Vector(1, 1));
        }
        Require(commands == fixture.Commands.Count, "Touch tap sent a geometry edit");
        using (var touch = new EditorTouch(canvas, 1104, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start); touch.Raise(UIElement.TouchMoveEvent, start + new Vector(30, 15));
            touch.Capture(null); touch.Raise(UIElement.TouchUpEvent, start + new Vector(30, 15));
        }
        Require(commands == fixture.Commands.Count, "Lost touch capture committed");
        using (var touch = new EditorTouch(canvas, 1105, start))
        using (var other = new EditorTouch(canvas, 1106, start + new Vector(25, 25)))
        {
            touch.Raise(UIElement.TouchDownEvent, start); touch.Raise(UIElement.TouchMoveEvent, start + new Vector(30, 15));
            other.Raise(UIElement.TouchDownEvent, start + new Vector(25, 25));
            other.Raise(UIElement.TouchUpEvent, start + new Vector(50, 50));
            touch.Raise(UIElement.TouchUpEvent, start + new Vector(30, 15));
        }
        Require(commands == fixture.Commands.Count, "Second contact committed a partial drag");
        using (var touch = new EditorTouch(canvas, 1107, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start); touch.Raise(UIElement.TouchMoveEvent, start + new Vector(30, 15));
            h.SelectedContent = h.Contents[0]; window.UpdateLayout();
            touch.Raise(UIElement.TouchUpEvent, start + new Vector(30, 15));
        }
        Require(commands == fixture.Commands.Count, "Changed selection committed an old drag");
        using (var touch = new EditorTouch(canvas, 1108, start))
        {
            touch.Raise(UIElement.TouchDownEvent, start); touch.Raise(UIElement.TouchMoveEvent, start + new Vector(30, 15));
            await Execute(vm, vm.ReleaseCommand);
            touch.Raise(UIElement.TouchUpEvent, start + new Vector(30, 15));
        }
        Require(commands == fixture.Commands.Count && !canvas.CanManipulate, "Lease loss committed a touch gesture");
        await Execute(vm, vm.AcquireCommand);
        Console.WriteLine("Hiperwall pointer cases passed (synthetic WPF input; physical touchscreen not tested).");
    }
}
