using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IntegratedContro.App;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunExistingHiperwallSources(MainWindow window, MainViewModel vm,
        HiperwallCanvas canvas, HiperwallEditorFixture fixture, Func<AsyncCommand, Task> hiper)
    {
        var h = vm.Hiperwall;
        var original = fixture.Server.Instances.Replace("<zone>zone-1</zone>", "");
        var walls = fixture.Server.Walls;
        var commands = fixture.Commands.Count;
        void Mouse(string method, params object[] values) => typeof(HiperwallCanvas)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, values);
        async Task Reload(string instances)
        {
            fixture.Server.Instances = instances;
            await hiper(h.RefreshCommand);
            h.Selected = null; h.TargetZone = null;
            canvas.FitAll(); window.UpdateLayout();
        }

        // Neither path opens any content or selects a destination Zone first.
        foreach (var touchInput in new[] { false, true })
        {
            await Reload(original);
            await Execute(vm, vm.ReleaseCommand);
            h.SelectedInstance = h.Instances.Single();
            Require(!h.CanMove, "Existing source editable without lease");
            await Execute(vm, vm.AcquireCommand);
            Require(h.CanMove && h.TargetZone?.Item.Id == "zone-1", "Acquire did not enable existing source");
            h.Selected = null; h.TargetZone = null; window.UpdateLayout();
            var start = canvas.ToScreen(-960, 0); var scale = canvas.ViewScale;
            if (touchInput)
            {
                using var touch = new EditorTouch(canvas, 1190, start);
                touch.Raise(UIElement.TouchDownEvent, start);
                Require(h.CanMove && ReferenceEquals(touch.Captured, canvas), "First touch failed before adding content");
                touch.Raise(UIElement.TouchMoveEvent, start + new Vector(18, 8));
                Require(commands == fixture.Commands.Count, "Existing touch sent before release");
                touch.Raise(UIElement.TouchUpEvent, start + new Vector(18, 8));
            }
            else
            {
                Mouse("BeginEditGesture", start);
                Require(h.CanMove && canvas.IsMouseCaptured, "First mouse drag failed before adding content");
                Mouse("UpdateEditGesture", start + new Vector(18, 8));
                Require(commands == fixture.Commands.Count, "Existing mouse sent before release");
                Mouse("FinishEditGesture");
            }
            await Wait(() => !h.IsBusy);
            Require(fixture.Commands.Count == ++commands && h.Instances.Single().TryRectangle(out var moved, out _) &&
                Math.Abs(moved.CenterX + 960 - 18 / scale) < .001 &&
                Math.Abs(moved.CenterY - 8 / scale) < .001, $"Existing source first drag failed: touch={touchInput}; commands={fixture.Commands.Count}/{commands}; canvas={canvas.ActualWidth}x{canvas.ActualHeight}; scale={scale}; position={h.Instances.Single().Item.Fields.GetValueOrDefault("position")}; size={h.Instances.Single().Item.Fields.GetValueOrDefault("size")}");
            Require(fixture.Commands.All(c => c.Attribute("type")?.Value == "change"), "Existing drag needed an open command");
        }

        await Reload(original.Replace("<size>640,360</size>", "<size>4000,2200</size>"));
        h.SelectedInstance = h.Instances.Single();
        Require(h.CanMove && h.TargetZone?.Item.Id == "zone-1", "Oversized source center was not resolved");
        await Reload(original.Replace("<Instance>", "<zone>zone-2</zone><Instance>"));
        h.SelectedInstance = h.Instances.Single();
        Require(h.TargetZone?.Item.Id == "zone-2", "Explicit source Zone lost to geometry inference");

        // An automatically resolved target must not leak to a different unresolved source.
        await Reload(original.Replace("</Objects>", original.Replace("<Objects>", "").Replace("external-1", "outside-1")
            .Replace("<position>-960,0</position>", "<position>5000,-5000</position>")));
        h.SelectedInstance = h.Instances.Single(i => i.Item.Id == "external-1");
        Require(h.CanMove, "Known source failed before switching selection");
        h.SelectedInstance = h.Instances.Single(i => i.Item.Id == "outside-1");
        Require(!h.CanMove, "Automatically inferred Zone leaked to another source");

        fixture.Server.Walls = walls.Replace("<left>0</left>", "<left>-1920.5</left>");
        await Reload(original); h.SelectedInstance = h.Instances.Single();
        Require(!h.CanMove && h.EditHint.Contains("Zone 정보가 없습니다"), "Overlapping Zones chose an arbitrary target");
        var ambiguous = canvas.ToScreen(-960, 0);
        Mouse("BeginEditGesture", ambiguous); Mouse("UpdateEditGesture", ambiguous + new Vector(25, 10)); Mouse("FinishEditGesture");
        Require(fixture.Commands.Count == commands, "Ambiguous Zone sent a command");
        h.TargetZone = h.Zones[1];
        Require(h.CanMove, "Explicit target did not enable ambiguous source");

        fixture.Server.Walls = walls;
        await Reload(original.Replace("<position>-960,0</position>", "<position>5000,-5000</position>"));
        h.SelectedInstance = h.Instances.Single();
        Require(!h.CanMove, "Out-of-Zone source picked an arbitrary target");
        await Reload(original);
        Console.WriteLine("Existing Hiperwall sources: first mouse/touch drag after acquire without opening content; unique/oversized/explicit/ambiguous/outside Zone cases PASS.");
    }

    private static void RequireInstanceRowsVisible(HiperwallView view)
    {
        var grid = (DataGrid)view.FindName("InstanceGrid");
        var scroll = (ScrollViewer)view.FindName("LiveEditorScroll");
        grid.ScrollIntoView(grid.Items[0]); grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        var headers = FindAll<DataGridColumnHeader>(grid).Where(c => c.Column is not null).ToArray();
        Require(row is not null && headers.Length == 2, "Instance table did not render its headers/first row");
        foreach (var element in headers.Cast<FrameworkElement>().Append(row!))
        {
            var bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
            Require(element.ActualHeight >= 25 && bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight &&
                bounds.Left >= 0 && bounds.Right <= scroll.ViewportWidth, "Instance table headers/row clipped in editor viewport");
        }
        var cells = FindAll<DataGridCell>(row!).ToArray();
        Require(cells.Length == 2 && cells.All(c => c.ActualWidth >= 80 && FindAll<TextBlock>(c).Any(t => !string.IsNullOrWhiteSpace(t.Text))),
            "Content name/instance ID cells are blank or too narrow");
        var rowsViewport = Find<DataGridRowsPresenter>(grid)!;
        Require(rowsViewport.ActualHeight >= grid.RowHeight * 2, $"Instance list cannot show two rows: viewport={rowsViewport.ActualHeight}, row={grid.RowHeight}, grid={grid.ActualHeight}");
    }
}
