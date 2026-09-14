using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private sealed class EditorTouch : TouchDevice, IDisposable
    {
        private readonly UIElement _root;
        private Point _point;
        private TouchAction _action;
        public EditorTouch(UIElement root, int id, Point point) : base(id)
        { _root = root; _point = point; SetActiveSource(PresentationSource.FromVisual(root)); Activate(); }
        public void Raise(RoutedEvent routedEvent, Point point)
        {
            _point = point;
            _action = routedEvent == UIElement.TouchDownEvent ? TouchAction.Down : routedEvent == UIElement.TouchUpEvent ? TouchAction.Up : TouchAction.Move;
            var args = new TouchEventArgs(this, Environment.TickCount) { RoutedEvent = routedEvent };
            _root.RaiseEvent(args);
            Require(args.Handled, "Touch event was not handled: " + routedEvent.Name);
        }
        public override TouchPoint GetTouchPoint(IInputElement relativeTo)
        {
            var point = relativeTo is UIElement element ? _root.TranslatePoint(_point, element) : _point;
            return new TouchPoint(this, point, new Rect(point, new Size(8, 8)), _action);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => [GetTouchPoint(relativeTo)];
        public void Dispose() { Capture(null); Deactivate(); }
    }
    private static async Task RunHiperwallEditing()
    {
        await using var fixture = new HiperwallEditorFixture();
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !h.IsBusy); Require(command.CanExecute(null), "Editor command unavailable: " + h.EditHint + " / " + h.Message);
            command.Execute(null); await Wait(() => !h.IsBusy);
            Require(!h.Message.StartsWith("작업 실패"), h.Message);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand); await Wait(() => !h.IsBusy);
            await Hiper(h.LoadSettingsCommand); h.Name = "편집 검증 Controller"; h.Endpoint = fixture.Server.Endpoint;
            h.User = "3"; h.Authentication = HiperwallAuthentication.Token; h.TimeoutText = "3000";
            h.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand); await Execute(vm, vm.RefreshCommand); await Wait(() => !h.IsBusy);
            await Hiper(h.RefreshCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HiperwallTab"); window.UpdateLayout();
            var view = (IntegratedContro.App.HiperwallView)window.FindName("HiperwallInventory");
            var canvas = (HiperwallCanvas)view.FindName("WallCanvas");
            // A Controller may provide geometry without an Object-level zone.
            fixture.Server.Instances = fixture.Server.Instances.Replace("<zone>zone-1</zone>", "");
            await Hiper(h.RefreshCommand);
            h.TargetZone = h.Zones[0]; h.SelectedInstance = h.Instances[0];
            Require(h.CanMove && h.TargetZone?.Item.Id == "zone-1", "Selecting a zone-less instance discarded the explicitly chosen Zone");
            h.SelectedContent = h.Contents[0]; h.TargetZone = h.Zones[0];
            Require(h.EditWidth == "2560" && h.EditHeight == "1440", "Native source dimensions lost");
            await Hiper(h.AddContentCommand);
            Require(h.Instances.Count == 2 && h.SelectedInstance is not null, "Open did not refresh/select new instance: " + h.Message);
            var id = h.SelectedInstance!.Item.Id;
            Require(h.SelectedInstance.TryRectangle(out var opened, out _) && opened.Width == 2560, "Large source was implicitly shrunk to Zone");
            h.EditX = "-250.25"; h.EditY = "700.5"; h.EditWidth = "960"; h.EditHeight = "540";
            await Hiper(h.ApplyGeometryCommand);
            Require(h.SelectedInstance!.TryRectangle(out var moved, out _) && moved.CenterX == -250.25 && moved.CenterY == 700.5, "Numeric geometry not applied");
            h.EditVolume = 37; await Hiper(h.ApplyVolumeCommand); await Hiper(h.MuteCommand);
            Require(HiperwallEditing.TryAudio(h.SelectedInstance!.Item, out var volume, out var muted) && volume == 37 && muted, "Audio state did not round-trip");
            await Hiper(h.UnmuteAllCommand);
            Require(HiperwallEditing.TryAudio(h.SelectedInstance!.Item, out _, out muted) && !muted, "Global unmute failed");
            h.TargetZone = h.Zones[1]; await Hiper(h.CenterInZoneCommand);
            Require(h.SelectedInstance!.TryRectangle(out moved, out _) && moved.Width == 960 && moved.CenterX == 960, "Zone move changed size");
            window.UpdateLayout(); canvas.FitAll(); window.UpdateLayout();
            await RunHiperwallPointers(window, vm, canvas, fixture, id!);
            await RunHiperwallZoneShortcuts(window, vm, view, canvas, fixture, id!);
            window.UpdateLayout();
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            Capture(window, Path.Combine(output, "hiperwall-editor.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Require(canvas.ActualWidth >= 300 && canvas.ActualHeight >= 70, "Editor unusable at minimum window size");
            Require(((ComboBox)view.FindName("ContentTypePicker")).SelectedItem is not null, "Content type selection lost after refresh");
            var closeAll = (Button)view.FindName("CloseAllButton");
            var buttonPosition = closeAll.TransformToAncestor(view).Transform(new Point());
            Require(buttonPosition.Y + closeAll.ActualHeight <= view.ActualHeight, "Global controls clipped at minimum window size");
            var zoneStrip = (ScrollViewer)view.FindName("ZoneShortcutScroll");
            var stripPosition = zoneStrip.TransformToAncestor(view).Transform(new Point());
            Require(zoneStrip.ActualHeight >= 36 && stripPosition.Y + zoneStrip.ActualHeight <= view.ActualHeight, "Zone buttons clipped at minimum window size");
            Capture(window, Path.Combine(output, "hiperwall-editor-small.png"));
            var commands = fixture.Commands.Count;
            h.ConfirmCloseAll = _ => false; await Hiper(h.CloseAllCommand);
            Require(commands == fixture.Commands.Count, "Cancel close-all sent commands");
            await Execute(vm, vm.ReleaseCommand);
            Require(!h.AddContentCommand.CanExecute(null) && !h.ApplyGeometryCommand.CanExecute(null) && !h.MuteCommand.CanExecute(null), "Lease release left edit actions enabled");
            await Execute(vm, vm.AcquireCommand); h.ConfirmCloseAll = _ => true;
            await Hiper(h.CloseAllCommand); Require(h.Instances.Count == 0, "Close all left fixture instances");
            Require(h.EditHistory.Count >= 7 && h.EditHistory.All(r => !r.Active), "Edit history missing completed requests");
            Require(!bindingLog.ToString().Contains("System.Windows.Data Error"), "Editor binding errors: " + bindingLog);
            var result = "PASS: add, audio, numeric geometry; mouse selection/gesture pipeline; routed WPF touch select/move/resize with a 44-DIP hit target; final release coordinates; exactly one command per drag; no command on tap, lost capture, changed selection, second touch, missing Zone or lost lease. Isolated HTTPS host and stateful fake Controller. Code-driven WPF mouse pipeline and synthetic TouchDevice; physical mouse/touchscreen/Controller not tested.";
            await File.WriteAllTextAsync(Path.Combine(output, "hiperwall-pointer-result.txt"), result);
            Console.WriteLine(result);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
