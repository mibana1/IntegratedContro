using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwallDeletion()
    {
        await using var fixture = new HiperwallEditorFixture();
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall;
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !h.IsBusy); Require(command.CanExecute(null), "Command unavailable: " + h.Message);
            command.Execute(null); await Wait(() => !h.IsBusy);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            await Hiper(h.LoadSettingsCommand); h.Name = "삭제 검증 Controller"; h.Endpoint = fixture.Server.Endpoint;
            h.User = "3"; h.Authentication = HiperwallAuthentication.Token; h.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand); await Execute(vm, vm.RefreshCommand); await Hiper(h.RefreshCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HiperwallTab"); window.UpdateLayout();
            var view = (IntegratedContro.App.HiperwallView)window.FindName("HiperwallInventory");
            var canvas = (HiperwallCanvas)view.FindName("WallCanvas");
            var grid = (DataGrid)view.FindName("InstanceGrid");
            var button = (Button)view.FindName("DeleteInstanceButton");
            async Task Focus(UIElement target)
            {
                if (target is FrameworkElement element) element.BringIntoView();
                window.Activate(); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                target.Focus(); Keyboard.Focus(target);
                Require(target.IsKeyboardFocusWithin, "Keyboard focus not established");
            }
            KeyEventArgs Press(UIElement target, Key key, bool repeat = false)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(args, [true]);
                target.RaiseEvent(args);
                return args;
            }
            async Task<string> Open(double x)
            {
                h.SelectedContent = h.Contents[0]; h.TargetZone = h.Zones[0];
                h.EditX = x.ToString(System.Globalization.CultureInfo.InvariantCulture); h.EditY = "0"; h.EditWidth = "320"; h.EditHeight = "180";
                await Hiper(h.AddContentCommand); return h.SelectedInstance!.Item.Id!;
            }
            var first = await Open(-300); var second = await Open(100); var third = await Open(500);
            var commands = fixture.Commands.Count;
            h.SelectedInstance = h.Instances.Single(i => i.Item.Id == first); await Focus(canvas);
            Require(button.IsEnabled, "Delete button disabled for a selected instance");
            Require(Press(canvas, Key.Delete, repeat: true).Handled && fixture.Commands.Count == commands,
                "Auto-repeat dispatched a close");
            // Cancel an uncommitted drag before closing; a later pointer-up must not send geometry.
            h.SelectedInstance!.TryRectangle(out var r, out _);
            var point = canvas.ToScreen(r.CenterX, r.CenterY);
            void Gesture(string method, params object[] values) => typeof(HiperwallCanvas)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(canvas, values);
            Gesture("BeginEditGesture", point); Gesture("UpdateEditGesture", point + new Vector(25, 10));
            Require(canvas.IsMouseCaptured, "Drag was not captured");
            Require(Press(canvas, Key.Delete).Handled, "Delete key not handled on canvas");
            Press(canvas, Key.Back); // Immediate second key while the first request is pending.
            Gesture("FinishEditGesture");
            await Wait(() => !h.IsBusy);
            Require(!canvas.IsMouseCaptured && fixture.Commands.Count == ++commands, "Delete duplicated a command or committed drag");
            Require(fixture.Commands.Last().Attribute("type")?.Value == "close" && fixture.Commands.Last().Element("id")?.Value == first,
                "Delete targeted a different instance");
            Require(h.Instances.All(i => i.Item.Id != first) && h.Instances.Any(i => i.Item.Id == second), "Selection removal affected another instance");

            h.SelectedInstance = h.Instances.Single(i => i.Item.Id == second); await Focus(grid);
            Require(!grid.CanUserDeleteRows && Press(grid, Key.Back).Handled, "Backspace did not route from the instance list");
            await Wait(() => !h.IsBusy); Require(fixture.Commands.Count == ++commands && fixture.Commands.Last().Element("id")?.Value == second,
                "Backspace did not close exactly its selected instance");

            h.SelectedInstance = h.Instances.Single(i => i.Item.Id == third); window.UpdateLayout();
            foreach (var name in new[] { "ContentSearch", "EditX" })
            {
                var input = (TextBox)view.FindName(name); await Focus(input);
                Require(!Press(input, Key.Back).Handled && !Press(input, Key.Delete).Handled, "Text input key was intercepted");
                input.SetCurrentValue(TextBox.TextProperty, "123"); input.CaretIndex = 3;
                EditingCommands.Backspace.Execute(null, input);
                Require(input.Text == "12" && fixture.Commands.Count == commands, "Text editing closed an instance");
            }
            h.Search = ""; h.Selected = null; window.UpdateLayout(); await Focus(canvas);
            Press(canvas, Key.Delete); Require(!button.IsEnabled && fixture.Commands.Count == commands, "Empty selection could delete");
            h.SelectedZone = h.Zones[0]; Press(canvas, Key.Back);
            Require(!button.IsEnabled && fixture.Commands.Count == commands, "Zone selection could delete");
            h.SelectedContent = h.Contents[0]; await Focus((ListBox)view.FindName("ContentList")); Press((ListBox)view.FindName("ContentList"), Key.Delete);
            Require(fixture.Commands.Count == commands, "Content library key closed a live instance");

            h.SelectedInstance = h.Instances.Single(i => i.Item.Id == third);
            await Execute(vm, vm.ReleaseCommand); await Focus(canvas); Press(canvas, Key.Delete);
            Require(!button.IsEnabled && fixture.Commands.Count == commands && h.Instances.Any(i => i.Item.Id == third),
                "Read-only session could delete");
            await Execute(vm, vm.AcquireCommand);
            // The normal host revision check must still reject stale selections.
            fixture.Server.Instances = fixture.Server.Instances.Replace("500,0", "510,0");
            await Focus(canvas); Press(canvas, Key.Delete); await Wait(() => !h.IsBusy);
            Require(fixture.Commands.Count == commands && fixture.Server.Instances.Contains(third), "Stale instance was closed");
            await Hiper(h.RefreshCommand); h.SelectedInstance = h.Instances.Single(i => i.Item.Id == third);
            var output = Path.Combine(host.Root, "artifacts", "ui-smoke");
            window.UpdateLayout(); Capture(window, Path.Combine(output, "hiperwall-delete.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            var position = button.TransformToAncestor(view).Transform(new Point());
            Require(button.IsVisible && position.Y + button.ActualHeight <= view.ActualHeight, "Delete button clipped");
            Capture(window, Path.Combine(output, "hiperwall-delete-small.png"));
            Require(button.Command!.CanExecute(null), "Delete button command unavailable");
            await Click(vm, button); await Wait(() => !h.IsBusy);
            Require(fixture.Commands.Count == ++commands && fixture.Commands.Last().Element("id")?.Value == third, "Delete button failed");
            Require(h.Instances.Count == 1 && h.Instances[0].Item.Id == "external-1" && h.Contents.Count == 2,
                "Deletion removed an unselected instance or library content");
            listener.Flush(); Require(!bindingLog.ToString().Contains("System.Windows.Data Error"), "Delete binding errors: " + bindingLog);
            var result = "PASS: routed WPF Delete on canvas and Backspace in opened-instance list; actual delete button; exact instance close and refresh; auto-repeat and busy duplicate prevention; pending drag cancelled; search/numeric text editing preserved; no selection, Zone/library and read-only input blocked; stale revision rejected; other instances and source library preserved; compact rendering. Isolated HTTPS host and fake Controller; not physical keyboard/Controller verification.";
            await File.WriteAllTextAsync(Path.Combine(output, "hiperwall-delete-result.txt"), result); Console.WriteLine(result);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
