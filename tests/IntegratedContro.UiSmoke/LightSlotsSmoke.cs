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
    private static async Task RunLightSlots()
    {
        await using var host = new HostProcess(); await host.Initialize();
        var window = new MainWindow(false) { Width = 1180, Height = 860 };
        var vm = (MainViewModel)window.DataContext;
        var output = Path.Combine(host.Root, "artifacts", "ui-smoke"); Directory.CreateDirectory(output);
        using var bindingLog = new StringWriter(); using var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show(); vm.Endpoint = host.Endpoint; vm.Fingerprint = host.Fingerprint;
            vm.LoginName = "admin"; vm.ReadLoginPassword = () => host.Password;
            await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            for (var i = 0; i < 3; i++)
            {
                await Execute(vm, vm.DeviceSettings.NewDeviceCommand);
                vm.DeviceSettings.SelectedModel = vm.DeviceSettings.Models.Single(m => m.Id == "virtual-light");
                vm.DeviceSettings.DeviceName = $"슬롯 조명 {i + 1}";
                await Execute(vm, vm.DeviceSettings.SaveDeviceCommand);
            }
            vm.DeviceViewIndex = 0; window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = FindAll<LightingSlotsView>(window).Single();
            var save = (Button)view.FindName("SaveLightSlotButton");
            var restore = (Button)view.FindName("RestoreLightSlotButton");
            var delete = (Button)view.FindName("DeleteLightSlotButton");
            var read = (Button)view.FindName("ReadSlotStatesButton");
            var name = (TextBox)view.FindName("LightSlotNameEditor");
            Require(vm.Lighting.LightSlots.Count == 6 && !restore.IsEnabled && !delete.IsEnabled && !save.IsEnabled,
                "Empty or unobserved slots allowed restore/delete/save");
            await Click(vm, read);
            Require(save.IsEnabled && vm.Lighting.Lights.All(c => c.Power == 0), "Read all did not prepare observed OFF states");
            var cards = vm.Lighting.Lights.ToArray();
            await Execute(vm, cards[0].PowerCommand);
            await Wait(() => cards[0].Power == 1 && cards[0].PowerCommand.CanExecute(null));
            await Execute(vm, vm.Lighting.EditLightOrderCommand);
            Require(!save.IsEnabled && !restore.IsEnabled, "Slot operations ignored an open layout draft");
            vm.Lighting.NewLightGroupName = "무대";
            await Execute(vm, vm.Lighting.AddLightGroupCommand);
            var group = vm.Lighting.LightGroups.Single(g => !g.IsDefault);
            Require(vm.Lighting.MoveLightToGroup(cards[1].Id, group.Id, null), "Could not assign first card to saved group");
            Require(vm.Lighting.MoveLightToGroup(cards[0].Id, group.Id, null), "Could not assign second card to saved group");
            await Execute(vm, vm.Lighting.SaveLightOrderCommand);
            var expectedOrder = vm.Lighting.Lights.Select(c => c.Id).ToArray();
            var expectedPower = vm.Lighting.Lights.ToDictionary(c => c.Id, c => c.Power);
            var slotButton = FindAll<Button>(view).Single(b => b.Name == "SelectLightSlot" && b.DataContext is LightSlotRow { Number: 2 });
            await Click(vm, slotButton);
            name.SetCurrentValue(TextBox.TextProperty, "야간 운영");
            await Execute(vm, vm.RefreshCommand);
            Require(name.Text == "야간 운영" && vm.Lighting.SelectedLightSlot?.Number == 2, "Polling lost slot selection/name");
            var jobCount = vm.JobManagement.Jobs.Count;
            await Click(vm, save);
            Require(vm.JobManagement.Jobs.Count == jobCount && vm.Lighting.SelectedLightSlot!.HasSaved &&
                vm.Lighting.SelectedLightSlot.Snapshot!.Layout.Groups.Single().Name == "무대" &&
                vm.Lighting.SelectedLightSlot.Snapshot.PowerStates.Count(s => s.Value == 1) == 1 &&
                vm.Lighting.SelectedLightSlot.Snapshot.PowerStates.Count(s => s.Value == 0) == 2,
                "Slot save sent control or lost group/mixed power states");

            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var viewport = new Rect(new Point(0, 0), window.RenderSize);
            foreach (var control in new FrameworkElement[] { view, save, restore, delete, name })
                Require(control.IsVisible && viewport.Contains(control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize))),
                    $"Slot control {control.Name} clipped at 1180x860");
            Capture(window, Path.Combine(output, "light-slots-saved.png"));
            await Execute(vm, vm.Lighting.EditLightOrderCommand);
            group = vm.Lighting.LightGroups.Single(g => !g.IsDefault);
            await Execute(vm, group.RemoveCommand);
            await Execute(vm, vm.Lighting.SaveLightOrderCommand);
            await Execute(vm, vm.Lighting.AllLightsOnCommand);
            await Wait(() => vm.Lighting.Lights.All(c => c.Power == 1) && vm.JobManagement.Jobs.All(j => !j.Job.Active));
            Require(vm.Lighting.LightGroups.All(g => g.IsDefault), "Mutation did not remove saved grouping");
            await Execute(vm, vm.ReleaseCommand);
            Require(!save.IsEnabled && !restore.IsEnabled && !delete.IsEnabled, "Slots remained writable without ownership");
            await Execute(vm, vm.AcquireCommand);
            await Click(vm, restore);
            await Wait(() => vm.JobManagement.Jobs.Any(j => j.Job.Kind == JobKind.LightSlot) && vm.JobManagement.Jobs.All(j => !j.Job.Active));
            Require(vm.JobManagement.Jobs.Single(j => j.Job.Kind == JobKind.LightSlot).Job.Status == JobStatus.Completed,
                "Slot power restore did not complete");
            Require(vm.Lighting.Lights.Select(c => c.Id).SequenceEqual(expectedOrder) &&
                vm.Lighting.LightGroups.Single(g => !g.IsDefault).Name == "무대" &&
                vm.Lighting.Lights.All(c => c.Power == expectedPower[c.Id]),
                "Restore lost saved group/order or mixed ON/OFF");
            window.UpdateLayout(); Capture(window, Path.Combine(output, "light-slots-restored.png"));
            await Click(vm, (Button)window.FindName("ThemeToggleButton"));
            window.UpdateLayout(); Capture(window, Path.Combine(output, "light-slots-dark.png"));
            await Click(vm, (Button)window.FindName("ThemeToggleButton"));
            await Execute(vm, vm.LogoutCommand); await Execute(vm, vm.LoginCommand); await Execute(vm, vm.AcquireCommand);
            await Click(vm, slotButton);
            Require(vm.Lighting.SelectedLightSlot!.Snapshot!.Name == "야간 운영" &&
                vm.Lighting.Lights.All(c => c.Power == expectedPower[c.Id]), "Reconnect lost saved slot or restored state");
            await Click(vm, delete);
            Require(!restore.IsEnabled && !delete.IsEnabled && vm.Lighting.Lights.All(c => c.Power == expectedPower[c.Id]) &&
                vm.Lighting.LightGroups.Single(g => !g.IsDefault).Name == "무대", "Delete changed current power/layout");
            listener.Flush(); Require(string.IsNullOrWhiteSpace(bindingLog.ToString()), "Light slot binding warnings: " + bindingLog);
            await File.WriteAllTextAsync(Path.Combine(output, "light-slots-result.txt"),
                "PASS: six slots; empty/unknown-state gating; read all; group/order/mixed ON/OFF save without control; polling name/selection; current admin ownership; restore group/order and absolute mixed power through a job; reconnect; delete preserves current layout/power; 1180x860 controls visible; light/dark captures; no binding warnings. Isolated HTTPS host, virtual devices only.");
            Console.WriteLine("Light slots WPF smoke PASS.");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await vm.CloseAsync(); window.Close(); }
    }
}
