using IntegratedContro.App;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class LightingViewModelTests
{
    [Fact]
    public void Card_request_freezes_visible_target_and_observation_without_optimistic_power()
    {
        var host = new FeatureHostFake(); var vm = host.Lighting();
        var card = Assert.Single(vm.Lights);
        host.Execute(card.PowerCommand);
        var request = Assert.Single(host.Submissions);
        Assert.Equal(0, request.Value);
        Assert.Equal(new CardPowerExpectation(card.Id, card.Device.PcId, 2, 3, 1, host.ObservedAt), request.CardPower);
        Assert.Equal(7, request.Generation);
        Assert.True(card.IsOn);
        host.Publish(host.State with { Devices = [card.Device with { PcName = "Renamed PC", Version = 4 }] });
        Assert.Same(card, vm.Lights[0]);
        Assert.Equal("Renamed PC", card.PcName);
        Assert.Equal(2, request.CardPower!.DeviceVersion);
        Assert.Single(host.Submissions);
    }

    [Fact]
    public void Group_draft_is_local_survives_disconnect_and_saves_original_layout_version()
    {
        var host = new FeatureHostFake(); var vm = host.Lighting();
        host.Execute(vm.EditLightOrderCommand);
        vm.NewLightGroupName = "Stage"; host.Execute(vm.AddLightGroupCommand);
        var group = vm.LightGroups.Single(g => !g.IsDefault);
        Assert.True(vm.MoveLightToGroup(vm.Lights[0].Id, group.Id, null));
        host.Context = host.Context with { Connected = false, CanControl = false, CanConfigure = false };
        host.Publish(host.State);
        Assert.Same(group, vm.LightGroups[0]);
        Assert.Single(group.Cards);
        Assert.False(vm.SaveLightOrderCommand.CanExecute(null));
        Assert.False(vm.MoveLightToGroup(vm.Lights[0].Id, Guid.Empty, null));
        host.Context = host.Context with { Connected = true, CanControl = true, CanConfigure = true };
        host.Publish(host.State with { LightLayout = host.State.LightLayout with { Version = 10 } });
        host.Execute(vm.SaveLightOrderCommand);
        Assert.Equal(5, Assert.Single(host.LayoutSaves).ExpectedVersion);
        Assert.Empty(host.Submissions);
        Assert.Empty(host.Batches);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("disconnected")]
    [InlineData("readonly")]
    [InlineData("uncertain")]
    [InlineData("forbidden")]
    [InlineData("legacy")]
    [InlineData("busy")]
    [InlineData("closing")]
    public void Context_changes_disable_card_and_notify_its_command(string reason)
    {
        var host = new FeatureHostFake(); var vm = host.Lighting(); var card = vm.Lights[0];
        var notifications = 0; card.PowerCommand.CanExecuteChanged += (_, _) => notifications++;
        host.Context = reason switch
        {
            "pending" => host.Context with { HasPending = true },
            "disconnected" => host.Context with { Connected = false, CanControl = false, CanConfigure = false },
            "readonly" => host.Context with { CanControl = false, CanConfigure = false },
            "busy" => host.Context with { Busy = true },
            "closing" => host.Context with { Closing = true },
            _ => host.Context
        };
        host.Publish(reason switch
        {
            "uncertain" => host.State with { UncertainDevices = [card.Id] },
            "forbidden" => host.State with { ControllableDeviceIds = [] },
            "legacy" => host.State with { LightCardsSupported = false },
            _ => host.State
        });
        Assert.True(notifications > 0);
        Assert.False(card.PowerCommand.CanExecute(null));
        card.PowerCommand.Execute(null);
        Assert.Empty(host.Submissions);
    }

    [Fact]
    public void Batch_freezes_group_members_and_navigation_reports_only_device_id()
    {
        var host = new FeatureHostFake(); var vm = host.Lighting();
        Guid? selected = null; vm.DeviceDetailsRequested += id => selected = id;
        host.Execute(vm.Lights[0].DetailsCommand);
        Assert.Equal(vm.Lights[0].Id, selected);
        host.Execute(vm.LightGroups[0].OffCommand);
        var batch = Assert.Single(host.Batches);
        Assert.Equal(Guid.Empty, batch.GroupId);
        Assert.Equal(5, batch.LayoutVersion);
        Assert.Equal(0, batch.Value);
        Assert.Equal(vm.Lights[0].Id, Assert.Single(batch.Targets).Expected.DeviceId);
        host.Execute(vm.EditLightOrderCommand);
        vm.NewLightGroupName = "Discard"; host.Execute(vm.AddLightGroupCommand);
        host.Execute(vm.CancelLightOrderCommand);
        Assert.True(Assert.Single(vm.LightGroups).IsDefault);
        Assert.Empty(host.LayoutSaves);
    }

    [Fact]
    public void Logout_discards_layout_edit_and_other_instances_keep_their_own_drafts()
    {
        var host = new FeatureHostFake(); var first = host.Lighting(); var second = host.Lighting();
        host.Execute(first.EditLightOrderCommand);
        first.NewLightGroupName = "Only first"; host.Execute(first.AddLightGroupCommand);
        Assert.True(Assert.Single(second.LightGroups).IsDefault);
        first.UpdateContext(new());
        Assert.Empty(first.Lights); Assert.Empty(first.LightGroups); Assert.False(first.EditingLightOrder);
        Assert.Single(second.Lights);
    }
}

public sealed class ScenarioEditorViewModelTests
{
    [Fact]
    public void Polling_preserves_inputs_but_target_replacement_clears_settings_and_conditions()
    {
        var host = new FeatureHostFake(); var vm = host.Scenario();
        vm.SelectedScenarioTarget = vm.ScenarioTargets[0];
        var setting = vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Brightness);
        setting.ValueText = "invalid draft"; vm.ConditionOperationText = "Power"; vm.ConditionPowerValue = 1;
        vm.DelayMsText = "bad"; vm.TimeoutMs = 5000;
        host.Publish(JsonDefaults.Copy(host.State) with { Revision = 2 });
        Assert.Same(setting, vm.ScenarioSettings.Single(s => s.Operation == DeviceOperation.Brightness));
        Assert.Equal("invalid draft", setting.ValueText); Assert.Equal("bad", vm.DelayMsText);
        Assert.Equal("1", vm.ConditionValueText);
        host.Publish(host.State with { Devices = [host.State.Devices[0] with { ConnectionId = "replacement" }] });
        Assert.All(vm.ScenarioSettings, s => Assert.False(s.Included));
        Assert.Equal("", vm.ConditionOperationText); Assert.Equal("", vm.ConditionValueText);
        Assert.Empty(host.Submissions);
    }

    [Fact]
    public void Duplicate_steps_move_by_index_and_saved_execution_ignores_invalid_edit_inputs()
    {
        var host = new FeatureHostFake(); var vm = host.Scenario();
        var first = new ScenarioStep("light", DeviceOperation.Power, 1);
        var duplicate = first with { };
        vm.DraftSteps.Add(first); vm.DraftSteps.Add(duplicate); vm.SelectedDraftStepIndex = 1;
        host.Execute(vm.MoveStepFirstCommand);
        Assert.Same(duplicate, vm.DraftSteps[0]); Assert.Same(first, vm.DraftSteps[1]);
        Assert.Equal(0, vm.SelectedDraftStepIndex);
        vm.ScenarioName = "Steps"; host.Execute(vm.SaveScenarioCommand);
        vm.SelectedScenario = Assert.Single(vm.Scenarios);
        vm.DelayMsText = "invalid"; vm.TimeoutMsText = "";
        Assert.False(vm.AddStepCommand.CanExecute(null));
        host.Execute(vm.RunScenarioCommand);
        Assert.Equal(vm.SelectedScenario.Id, Assert.Single(host.Submissions).ScenarioId);
        Assert.Null(host.Submissions[0].RoleId);
        Assert.Equal(3000, host.ScenarioSaves[0].Steps[0].TimeoutMs);
        host.Execute(vm.RemoveStepCommand);
        Assert.Same(first, Assert.Single(vm.DraftSteps));
        Assert.Equal(2, host.ScenarioSaves[0].Steps.Length);
    }

    [Fact]
    public void Features_and_editor_instances_do_not_share_timing_or_drafts()
    {
        var host = new FeatureHostFake(); var first = host.Scenario(); var second = host.Scenario();
        var lighting = host.Lighting();
        first.SelectedScenarioTarget = first.ScenarioTargets[0];
        first.ScenarioSettings[0].Value = 1; first.DelayMs = 234; first.TimeoutMs = 1234;
        host.Execute(first.AddStepCommand);
        Assert.Equal(234, Assert.Single(first.DraftSteps).DelayBeforeMs);
        Assert.Equal(0, second.DelayMs); Assert.Equal(3000, second.TimeoutMs); Assert.Empty(second.DraftSteps);
        host.Execute(lighting.EditLightOrderCommand);
        Assert.Single(first.DraftSteps);
        Assert.Empty(host.Submissions);
    }

    [Theory]
    [InlineData(ScenarioStepKind.DeviceCommand, false)]
    [InlineData(ScenarioStepKind.WaitUntil, true)]
    [InlineData(ScenarioStepKind.DisplayLayout, true)]
    public void Timing_validation_belongs_to_the_selected_step_kind(ScenarioStepKind kind, bool valid)
    {
        var host = new FeatureHostFake(); var vm = host.Scenario();
        vm.DraftStepKind = kind; vm.TimeoutMsText = "60000";
        Assert.Equal(valid, vm.AddStepCommand.CanExecute(null));
        vm.DelayMsText = "2147483648";
        Assert.False(vm.AddStepCommand.CanExecute(null));
        Assert.NotEmpty(vm.ScenarioTimingError);
    }

    [Fact]
    public void Save_failure_keeps_identity_version_and_draft_for_explicit_retry()
    {
        var host = new FeatureHostFake(); var vm = host.Scenario();
        vm.ScenarioName = "Keep"; vm.DraftSteps.Add(new("light", DeviceOperation.Power, 0));
        host.Execute(vm.SaveScenarioCommand);
        vm.ScenarioName = "Changed"; host.FailSave = true;
        vm.SaveScenarioCommand.Execute(null);
        Assert.IsType<InvalidOperationException>(host.Error);
        Assert.Single(vm.DraftSteps); Assert.Equal("Changed", vm.ScenarioName);
        host.FailSave = false; host.Execute(vm.SaveScenarioCommand);
        Assert.Single(host.ScenarioSaves.Select(s => s.Id).Distinct());
        Assert.Equal(new[] { 0, 1, 1 }, host.ScenarioSaves.Select(s => s.ExpectedVersion));
    }

    [Fact]
    public void Deletion_checks_active_work_and_preserves_a_different_draft()
    {
        var host = new FeatureHostFake(); var vm = host.Scenario();
        vm.ScenarioName = "First"; vm.DraftSteps.Add(new("light", DeviceOperation.Power, 1)); host.Execute(vm.SaveScenarioCommand);
        var first = Assert.Single(vm.Scenarios);
        host.Execute(vm.NewScenarioCommand);
        vm.ScenarioName = "Second"; vm.DraftSteps.Add(new("light", DeviceOperation.Power, 0)); host.Execute(vm.SaveScenarioCommand);
        var second = vm.Scenarios.Single(s => s.Id != first.Id);
        vm.SelectedScenario = first;
        var job = new Job { RequestFingerprint = "test", Kind = JobKind.Scenario,
            Snapshot = new(host.State.SiteId, "Virtual", Guid.NewGuid(), Guid.NewGuid(), "test", Guid.NewGuid(), Guid.NewGuid(), "PC",
                7, host.ObservedAt, host.ObservedAt.AddHours(1), first.Id, first.Version, first.Name, []) };
        host.Publish(host.State with { Jobs = [job] });
        Assert.False(vm.DeleteScenarioCommand.CanExecute(null)); Assert.Contains("진행 중", vm.ScenarioDeletionHint);
        host.Publish(host.State with { Jobs = [] }); host.Execute(vm.DeleteScenarioCommand);
        Assert.Equal(second.Id, Assert.Single(vm.Scenarios).Id);
        Assert.Equal("Second", vm.ScenarioName); Assert.Equal(0, Assert.Single(vm.DraftSteps).Value);
        vm.SelectedScenario = second; host.Execute(vm.DeleteScenarioCommand);
        Assert.Empty(vm.DraftSteps); Assert.Equal("", vm.ScenarioName);
    }
}

// In-memory typed ports: no MainViewModel, WPF application, credentials, SQLite, or network.
internal sealed class FeatureHostFake : ILightingHost, IScenarioHost
{
    public DateTimeOffset ObservedAt { get; } = DateTimeOffset.Parse("2026-09-17T01:00:00Z");
    public StateView State { get; private set; }
    public FeatureContext Context { get; set; }
    private event Action<FeatureContext>? Changed;
    public List<SubmitRequest> Submissions { get; } = [];
    public List<LightBatchRequest> Batches { get; } = [];
    public List<LightOrderRequest> LayoutSaves { get; } = [];
    public List<ScenarioRequest> ScenarioSaves { get; } = [];
    public Exception? Error { get; private set; }
    public bool FailSave { get; set; }
    public FeatureHostFake()
    {
        var device = new DeviceConfig(Guid.NewGuid(), Guid.NewGuid(), "Light PC", "Stage light", "test", "light", 2, true, VirtualFault.None, 0);
        State = new(Guid.NewGuid(), "Test", 1, new() { Generation = 7 },
            new(Guid.NewGuid(), Guid.NewGuid(), "test", AccountRole.Administrator, Guid.NewGuid(), "PC"),
            [device], new() { [device.Id] = new() { Simulated = new() { [DeviceOperation.Power] = new(1, ObservedAt) } } },
            [new("light", device.Id, 3)], [], [], [], [], [],
            [new("light", "Light", [new(DeviceOperation.Power, 0, 1, ""), new(DeviceOperation.Brightness, 0, 100, "%")], DeviceCategory.Lighting)], 15)
        {
            LightLayout = new(5, [device.Id]), LightCardsSupported = true, LightGroupsSupported = true,
            LightBatchSupported = true, ControllableDeviceIds = [device.Id], ScenarioExtensionsSupported = true, ScenarioDeletionSupported = true
        };
        Context = new(State, Connected: true, CanControl: true, CanConfigure: true);
    }
    public LightingViewModel Lighting() { var vm = new LightingViewModel(this); Changed += vm.UpdateContext; vm.UpdateContext(Context); return vm; }
    public ScenarioEditorViewModel Scenario() { var vm = new ScenarioEditorViewModel(this); Changed += vm.UpdateContext; vm.UpdateContext(Context); return vm; }
    public void Publish(StateView state) { State = state; Context = Context with { State = state }; Changed?.Invoke(Context); }
    public async Task RunAsync(Func<Task> action)
    {
        Error = null; Context = Context with { Busy = true }; Publish(State);
        try { await action(); }
        catch (Exception error) { Error = error; }
        finally { Context = Context with { Busy = false }; Publish(State); }
    }
    public void Execute(AsyncCommand command)
    {
        Assert.True(command.CanExecute(null)); command.Execute(null); Assert.Null(Error);
        Assert.False(Context.Busy); // All fake operations complete synchronously.
    }
    public void ReportStatus(string message) { }
    public Task SubmitAsync(SubmitRequest request) { Submissions.Add(request); return Task.CompletedTask; }
    public Task SubmitBatchAsync(LightBatchRequest request) { Batches.Add(request); return Task.CompletedTask; }
    public Task<LightSlot> SaveLightSlotAsync(SaveLightSlotRequest request)
    {
        if (FailSave) throw new InvalidOperationException("Save failed");
        var slot = new LightSlot(request.Number, request.ExpectedVersion + 1, request.Name.Trim(), State.LightLayout, [], ObservedAt);
        Publish(State with { LightSlots = [.. State.LightSlots.Where(s => s.Number != slot.Number), slot] });
        return Task.FromResult(slot);
    }
    public Task<LightSlot> DeleteLightSlotAsync(DeleteLightSlotRequest request)
    {
        var slot = new LightSlot(request.Number, request.ExpectedVersion + 1, "", new(0, []), [], null);
        Publish(State with { LightSlots = [.. State.LightSlots.Where(s => s.Number != slot.Number), slot] });
        return Task.FromResult(slot);
    }
    public List<RestoreLightSlotRequest> SlotRestores { get; } = [];
    public Task RestoreLightSlotAsync(RestoreLightSlotRequest request) { SlotRestores.Add(request); return Task.CompletedTask; }
    public Task<DeviceState> ReconcileAsync(ReconcileRequest request) => Task.FromResult(State.DeviceStates[request.DeviceId]);
    public Task<LightLayout> SaveLayoutAsync(LightOrderRequest request)
    {
        LayoutSaves.Add(request);
        var layout = new LightLayout(request.ExpectedVersion + 1, request.DeviceIds) { Groups = request.Groups ?? [] };
        Publish(State with { LightLayout = layout }); return Task.FromResult(layout);
    }
    public Task<ScenarioDefinition> SaveAsync(ScenarioRequest request)
    {
        ScenarioSaves.Add(request);
        if (FailSave) throw new InvalidOperationException("Save failed");
        var saved = new ScenarioDefinition(request.Id, request.Name, request.ExpectedVersion + 1, request.Steps);
        Publish(State with { Scenarios = [.. State.Scenarios.Where(s => s.Id != saved.Id), saved] });
        return Task.FromResult(saved);
    }
    public Task DeleteAsync(DeleteScenarioRequest request)
    {
        Publish(State with { Scenarios = State.Scenarios.Where(s => s.Id != request.Id).ToArray() }); return Task.CompletedTask;
    }
}
