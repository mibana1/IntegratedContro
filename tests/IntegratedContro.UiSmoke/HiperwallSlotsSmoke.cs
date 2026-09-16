using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwallSlots()
    {
        // Keep omitting Zone even after open/change to exercise actual Controller responses.
        await using var fixture = new HiperwallEditorFixture { OmitInstanceZones = true };
        fixture.Server.Instances = fixture.Server.Instances.Replace("</Objects>", """
            <Object type="image"><name>폴더/이미지 &amp; 지도</name><zone>zone-2</zone>
            <Instance><id>external-2</id><position>500.25,-100.5</position><size>640.5,480.25</size><rotation>0</rotation><audio>100,unmuted</audio></Instance>
            </Object></Objects>
            """);
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false); var vm = (MainViewModel)window.DataContext; var h = vm.Hiperwall;
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        async Task Hiper(AsyncCommand command)
        {
            await Wait(() => !h.IsBusy); Require(command.CanExecute(null), "Hiperwall unavailable: " + h.Message);
            command.Execute(null); await Wait(() => !h.IsBusy);
        }
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            await Hiper(h.LoadSettingsCommand); h.Name = "저장 슬롯 검증"; h.Endpoint = fixture.Server.Endpoint;
            h.User = "3"; h.Authentication = HiperwallAuthentication.Token; h.ReadSecret = () => FakeHiperwallServer.FixtureSecret;
            await Hiper(h.SaveCommand); await Execute(vm, vm.RefreshCommand); await Hiper(h.RefreshCommand);
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HiperwallTab"); window.UpdateLayout();
            var view = (IntegratedContro.App.HiperwallView)window.FindName("HiperwallInventory");
            var slots = (ItemsControl)view.FindName("LayoutSlotButtons");
            var save = (Button)view.FindName("SaveLayoutSlot"); var delete = (Button)view.FindName("DeleteLayoutSlot");
            var restore = (Button)view.FindName("RestoreLayoutSlot");
            var buttons = FindAll<Button>(slots).ToArray();
            Require(buttons.Length == 6 && buttons.All(b => b.ActualHeight >= 44), "Six touch-sized slots missing");
            Button Slot(int n) => buttons.Single(b => ((HiperwallSlotRow)b.DataContext).Number == n);
            async Task Press(Button button) { await Click(vm, button); await Wait(() => !h.IsBusy); }
            Require(!delete.IsEnabled && !restore.IsEnabled && save.IsEnabled, "Empty slot actions incorrect");
            await Press(Slot(1)); await Press(save);
            var original = h.LayoutSlots[0].Snapshot!;
            Require(original.Placements.Length == 2 && fixture.Commands.IsEmpty && h.SlotMessage.Contains("저장 완료"),
                "Saving did not capture live or sent Controller writes");
            Require(h.LayoutSlots[0].IsSelected && delete.IsEnabled && restore.IsEnabled, "Saved slot state not reflected");
            h.UpdateSlots([], true);
            Require(h.LayoutSlots[0].Snapshot == original, "Older polling response erased fresh save");
            await Execute(vm, vm.RefreshCommand);
            Require(h.LayoutSlots[0].Snapshot!.Version == original.Version && h.SelectedSlot!.Number == 1, "Polling lost slot selection");

            h.SelectedInstance = h.Instances.Single(i => i.Item.Id == "external-2");
            h.TargetZone = h.Zones.Single(z => z.Item.Id == "zone-1");
            h.EditX = "-450.5"; h.EditY = "20.25"; h.EditWidth = "250.75"; h.EditHeight = "150.5";
            await Hiper(h.ApplyGeometryCommand);
            await Press(Slot(2)); var beforeSave = fixture.Commands.Count; await Press(save);
            Require(h.LayoutSlots[1].Snapshot!.Placements[1].Layout == new HiperwallLayout(-450.5, 20.25, 250.75, 150.5) &&
                fixture.Commands.Count == beforeSave, "Second slot did not capture modified LIVE");
            await Press(Slot(1)); var beforeRestore = fixture.Commands.Count; await Press(restore);
            Require(fixture.Commands.Skip(beforeRestore).Select(c => c.Attribute("type")!.Value)
                .SequenceEqual(new[] { "close", "close", "open", "open" }), "Restore did not replace current instances");
            Require(h.Instances.Count == 2 && h.Instances.All(i => i.Item.Id!.StartsWith("integrated-")), "Canvas retained replaced instances");
            foreach (var (row, expected) in h.Instances.Zip(original.Placements))
            {
                Require(row.TryRectangle(out var rect, out _) && HiperwallLayout.From(rect) == expected.Layout &&
                    HiperwallGeometry.ResolveInstanceZone(row.Item, h.Zones.Select(z => z.Item))?.Id == expected.ZoneId,
                    "Restored canvas differs from saved geometry/Zone");
            }
            var canvas = (HiperwallCanvas)view.FindName("WallCanvas");
            Require(h.CanvasItems.Count(i => i.Kind == HiperwallRowKind.Instance) == 2, "Restored state absent from canvas");
            window.Width = 1500; window.Height = 1000; window.UpdateLayout();
            Capture(window, Path.Combine(output, "hiperwall-slots.png"));
            window.Width = 1180; window.Height = 860; window.UpdateLayout();
            Capture(window, Path.Combine(output, "hiperwall-slots-small.png"));
            Require(canvas.ActualHeight >= 90 && restore.IsVisible && buttons.All(b => b.IsVisible && b.ActualWidth >= 90),
                "Compact layout clipped canvas or slot actions");
            var editorScroll = (ScrollViewer)view.FindName("LiveEditorScroll");
            editorScroll.ScrollToBottom(); window.UpdateLayout();
            var closeAll = (Button)view.FindName("CloseAllButton");
            var controlsY = closeAll.TransformToAncestor(editorScroll).Transform(new Point()).Y;
            Require(controlsY >= 0 && controlsY + closeAll.ActualHeight <= editorScroll.ViewportHeight,
                "Small-window lower controls cannot be reached by scrolling");
            Capture(window, Path.Combine(output, "hiperwall-slots-small-scrolled.png"));

            await Press(Slot(2)); var live = fixture.Server.Instances; var count = fixture.Commands.Count; await Press(delete);
            Require(!h.LayoutSlots[1].HasSaved && h.LayoutSlots[0].HasSaved && fixture.Commands.Count == count &&
                fixture.Server.Instances == live && !delete.IsEnabled && !restore.IsEnabled, "Delete changed current display or another slot");
            await Press(Slot(1)); await Execute(vm, vm.ReleaseCommand);
            Require(!save.IsEnabled && !delete.IsEnabled && !restore.IsEnabled &&
                save.ToolTip is string leaseHint && leaseHint.Contains("사용 시작"), "Lease guard or disabled reason missing");
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand);
            await Execute(vm, vm.AcquireCommand); await Hiper(h.RefreshCommand);
            Require(h.LayoutSlots[0].HasSaved && !h.LayoutSlots[1].HasSaved && h.Instances.Count == 2, "Reconnect lost slot/display state");
            await Press(Slot(1)); await Press(save);
            Require(h.LayoutSlots[0].Version == original.Version + 1, "Overwrite did not update selected slot");
            h.UpdateSlots(h.LayoutSlots.Where(s => s.Snapshot is not null).Select(s => s.Snapshot!).ToArray(), false);
            Require(!save.IsEnabled && !delete.IsEnabled && !restore.IsEnabled &&
                save.ToolTip is string supportHint && supportHint.Contains("ControlHost") &&
                ToolTipService.GetShowOnDisabled(save), "Unsupported host guard or disabled tooltip missing");
            h.UpdateSlots(h.LayoutSlots.Where(s => s.Snapshot is not null).Select(s => s.Snapshot!).ToArray(), true);
            Require(save.IsEnabled && delete.IsEnabled && restore.IsEnabled &&
                save.ToolTip is string enabledHint && !enabledHint.Contains("ControlHost"), "Slot actions or hint did not recover with supported host");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Slot binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "hiperwall-slots-result.txt"),
                "PASS: six stable 44+ DIP slot buttons, selection/highlight and empty state; actual save/delete/load buttons; authoritative live capture without writes; two Zone/source forms and fractional geometry; replacement close/open order and refreshed canvas; overwrite, delete-only metadata, polling/reconnect and stale response protection; ownership/old-host guards; full/compact rendering without binding errors. Isolated HTTPS host and fake Controller; code-driven WPF, physical touch/Controller not tested.");
            Console.WriteLine("Hiperwall storage slots WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
