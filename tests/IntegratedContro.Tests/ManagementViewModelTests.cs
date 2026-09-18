using IntegratedContro.App;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class ManagementViewModelTests
{
    [Fact]
    public void Device_editor_preserves_draft_row_identity_and_expected_version_across_poll_and_disconnect()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        vm.SelectedDevice = vm.Devices.Single(); host.Execute(vm.LoadDeviceCommand);
        var row = vm.SelectedDevice; var pcId = vm.PcIdText;
        vm.DeviceName = "draft"; vm.DeviceEndpoint = "typed endpoint"; vm.RoleName = "typed.role";
        host.Publish(host.State with { Devices = [host.State.Devices[0] with { Version = 9, Name = "external edit" }] });
        Assert.Same(row, vm.SelectedDevice); Assert.Equal("external edit", row!.Name);
        Assert.Equal("draft", vm.DeviceName); Assert.Equal("typed endpoint", vm.DeviceEndpoint);
        Assert.Equal("typed.role", vm.RoleName); Assert.Equal(2, vm.DeviceExpectedVersion);
        host.Context = host.Context with { Connected = false, CanControl = false, CanConfigure = false }; host.Publish(host.State);
        Assert.Equal("draft", vm.DeviceName); Assert.False(vm.SaveDeviceCommand.CanExecute(null));
        host.Context = host.Context with { Connected = true, CanControl = true, CanConfigure = true }; host.Publish(host.State);
        host.Fail = true; vm.SaveDeviceCommand.Execute(null);
        Assert.IsType<InvalidOperationException>(host.Error); Assert.Equal(2, vm.DeviceExpectedVersion);
        Assert.Equal("draft", vm.DeviceName);
        host.Fail = false; host.Execute(vm.SaveDeviceCommand);
        Assert.Equal(2, host.DeviceSaves.Count);
        Assert.All(host.DeviceSaves, saved => Assert.Equal(2, saved.ExpectedVersion));
        var request = host.DeviceSaves[1];
        Assert.Equal(row.Id, request.Id); Assert.Equal(Guid.Parse(pcId), request.PcId); Assert.Equal(2, request.ExpectedVersion);
        Assert.Equal("typed endpoint", request.Connection!.Endpoint);
    }

    [Fact]
    public void Device_editors_are_independent_and_session_change_clears_target_and_draft()
    {
        var host = new ManagementHostFake(); var pc = Guid.NewGuid();
        var first = host.Attach(new DeviceSettingsViewModel(host, pc)); var second = host.Attach(new DeviceSettingsViewModel(host, pc));
        first.SelectedDevice = first.Devices.Single(); host.Execute(first.LoadDeviceCommand);
        first.DeviceName = "private draft"; first.RoleName = "private.role";
        Assert.Equal("", second.DeviceName); Assert.Null(second.SelectedDevice);
        host.Publish(host.State with { Session = host.State.Session with { Id = Guid.NewGuid() } });
        Assert.Null(first.SelectedDevice); Assert.Equal("", first.DeviceName); Assert.Equal(0, first.DeviceExpectedVersion);
        Assert.Equal(pc.ToString(), first.PcIdText); Assert.Empty(first.AssignedRoles); Assert.Equal("", first.RoleName);
    }

    [Fact]
    public void Role_unassignment_uses_exact_version_and_old_row_cannot_act_after_selection_changes()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        vm.SelectedDevice = vm.Devices.Single(); var row = Assert.Single(vm.AssignedRoles);
        vm.SelectedDevice = null; Assert.False(row.UnassignCommand.CanExecute(null)); row.UnassignCommand.Execute(null);
        Assert.Empty(host.Unassignments);
        vm.SelectedDevice = vm.Devices.Single(); host.Execute(vm.AssignedRoles.Single().UnassignCommand);
        var request = Assert.Single(host.Unassignments);
        Assert.Equal("light", request.Id); Assert.Equal(3, request.ExpectedVersion); Assert.Equal(7, request.Generation);
        Assert.Empty(vm.AssignedRoles); Assert.Single(vm.Devices);
        Assert.False(row.UnassignCommand.CanExecute(null));
    }

    [Fact]
    public void Role_assignment_reports_only_the_saved_role_and_keeps_the_selected_device()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        vm.SelectedDevice = vm.Devices.Single(); var id = vm.SelectedDevice.Id; vm.RoleName = " room.light ";
        string? assigned = null; vm.RoleAssigned += role => assigned = role;
        host.Execute(vm.SaveRoleCommand);
        Assert.Equal("room.light", assigned); Assert.Equal(id, Assert.Single(host.RoleSaves).DeviceId);
        Assert.Equal(id, vm.SelectedDevice!.Id); Assert.Equal("room.light", vm.RoleName);
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("disconnected")]
    [InlineData("busy")]
    [InlineData("closing")]
    public void Management_writes_follow_the_shared_authority_and_busy_boundary(string reason)
    {
        var host = new ManagementHostFake(); var device = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        var account = host.Attach(new AccountManagementViewModel(host)); var jobs = host.Attach(new JobManagementViewModel(host));
        var control = host.Attach(new DeviceControlViewModel(host));
        device.SelectedDevice = device.Devices.Single(); control.SelectedRole = control.Roles.Single();
        var job = host.NewJob(); host.Publish(host.State with { Jobs = [job] }); jobs.SelectedJob = jobs.Jobs.Single();
        host.Context = reason switch
        {
            "readonly" => host.Context with { CanConfigure = false, CanControl = false },
            "disconnected" => host.Context with { Connected = false, CanConfigure = false, CanControl = false },
            "busy" => host.Context with { Busy = true },
            _ => host.Context with { Closing = true }
        };
        host.Publish(host.State);
        foreach (var command in new[] { device.SaveDeviceCommand, device.SaveRoleCommand, device.AssignedRoles.Single().UnassignCommand,
            account.CreateAccountCommand, account.UpdateAccountCommand, jobs.CancelCommand, control.SubmitCommand })
        { Assert.False(command.CanExecute(null)); command.Execute(null); }
        Assert.Empty(host.DeviceSaves); Assert.Empty(host.Creations); Assert.Empty(host.Cancellations); Assert.Empty(host.Submissions);
    }

    [Fact]
    public void Manual_input_survives_polling_and_does_not_share_device_editor_state()
    {
        var host = new ManagementHostFake(); var control = host.Attach(new DeviceControlViewModel(host));
        var settings = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        control.SelectedRole = control.Roles.Single(); control.CommandValueText = "bad"; control.DelayMsText = "typed";
        host.Publish(JsonDefaults.Copy(host.State));
        Assert.Equal("bad", control.CommandValueText); Assert.Equal("typed", control.DelayMsText);
        Assert.False(control.SubmitCommand.CanExecute(null));
        settings.DeviceName = "local draft"; control.CommandValue = 1; control.DelayMs = 22;
        host.Execute(control.SubmitCommand); var request = Assert.Single(host.Submissions);
        Assert.Equal("light", request.RoleId); Assert.Equal(7, request.Generation); Assert.Equal(22, request.DelayBeforeMs);
        Assert.Equal("local draft", settings.DeviceName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Account_creation_clears_password_on_success_or_failure_and_keeps_scope_explicit(bool fail)
    {
        var host = new ManagementHostFake { Fail = fail }; var vm = host.Attach(new AccountManagementViewModel(host));
        var target = Guid.NewGuid(); var cleared = 0;
        vm.NewAccountName = "operator"; vm.NewAccountRole = AccountRole.Operator;
        vm.AccountAllDevices = false; vm.AccountDeviceIds = target.ToString();
        vm.ReadNewPassword = () => "fixture-only-password"; vm.ClearNewPassword = () => cleared++;
        vm.CreateAccountCommand.Execute(null);
        Assert.Equal(1, cleared); var request = Assert.Single(host.Creations);
        Assert.Equal(target, Assert.Single(request.DeviceIds!)); Assert.False(request.AllDevices); Assert.Equal(7, request.Generation);
        Assert.Equal(fail, host.Error is not null);
    }

    [Fact]
    public void Account_permission_draft_survives_poll_and_disconnect_but_not_another_session()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new AccountManagementViewModel(host));
        vm.SelectedAccount = vm.Accounts.Single(); host.Execute(vm.LoadAccountCommand); var id = vm.SelectedAccount.Id;
        vm.AccountEnabled = false; vm.AccountAllDevices = false; vm.AccountDeviceIds = "invalid-guid";
        vm.NewAccountName = "draft"; var cleared = 0; vm.ClearNewPassword = () => cleared++;
        host.Publish(JsonDefaults.Copy(host.State));
        Assert.Equal(id, vm.SelectedAccount!.Id); Assert.Equal("invalid-guid", vm.AccountDeviceIds); Assert.False(vm.AccountEnabled);
        vm.UpdateAccountCommand.Execute(null); Assert.IsType<FormatException>(host.Error); Assert.Empty(host.PermissionChanges);
        host.Context = host.Context with { Connected = false, CanConfigure = false }; host.Publish(host.State);
        Assert.Equal("draft", vm.NewAccountName); Assert.Equal(0, cleared);
        host.Publish(host.State with { Session = host.State.Session with { Id = Guid.NewGuid() } });
        Assert.Null(vm.SelectedAccount); Assert.Equal("", vm.NewAccountName); Assert.Equal("", vm.AccountDeviceIds); Assert.Equal(1, cleared);
    }

    [Fact]
    public void Job_cancellation_and_confirmed_switch_use_selected_original_work_with_current_generation()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new JobManagementViewModel(host)); var job = host.NewJob();
        host.Publish(host.State with { Jobs = [job] }); vm.SelectedJob = vm.Jobs.Single();
        Assert.True(vm.SelectedJob.PreviousSession); Assert.Contains(job.Snapshot.SessionId.ToString(), vm.JobDetails);
        host.Execute(vm.CancelCommand); Assert.Equal(new JobActionRequest(7, job.Id), Assert.Single(host.Cancellations));
        vm.ConfirmManualSwitch = _ => false; host.Execute(vm.ManualSwitchCommand); Assert.Empty(host.Switches);
        vm.ConfirmManualSwitch = _ => true; host.Execute(vm.ManualSwitchCommand); Assert.Equal(job.Id, Assert.Single(host.Switches).JobId);
        host.Context = new(); vm.UpdateContext(host.Context);
        Assert.Empty(vm.Jobs); Assert.Null(vm.SelectedJob); Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Hiperwall_handover_routes_edit_cancellation_and_display_cleanup_without_double_counting()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new JobManagementViewModel(host));
        var owner = host.State.Session with { Id = Guid.NewGuid() }; var id = Guid.NewGuid();
        var receipt = new HiperwallEditReceipt { Request = new(id, 1, 1, HiperwallEditAction.Close), Requester = owner, Endpoint = "fixture",
            Steps = [new() { Command = new(HiperwallEditAction.Close, "instance"), State = HiperwallSendState.Pending }] };
        host.Publish(host.State with { OutstandingHiperwallEdits = [receipt], CanControlHiperwall = true });
        vm.SelectedHiperwallJob = vm.HiperwallJobs.Single(); host.Execute(vm.CancelHiperwallJobCommand);
        Assert.Equal(id, Assert.Single(host.EditCancellations).JobId);
        var display = new HiperwallDisplayJob { Request = new(id, 1, 1), Requester = owner, Name = "display", Endpoint = "fixture", Duration = new() };
        host.Publish(host.State with { OutstandingHiperwallDisplays = [display] });
        Assert.Single(vm.HiperwallJobs); Assert.StartsWith("이전 사용자 작업 1건", vm.PreviousSummary);
        host.Execute(vm.CancelHiperwallJobCommand); Assert.Equal(id, Assert.Single(host.DisplayStops).JobId);
        host.Publish(host.State with { Session = host.State.Session with { Id = Guid.NewGuid() } });
        Assert.Null(vm.SelectedHiperwallJob);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("disconnect")]
    [InlineData("closing")]
    public void Recovery_approval_requires_a_current_review_and_invalidates_it_at_authority_boundaries(string boundary)
    {
        var host = new ManagementHostFake(); host.BeginRecovery(); var vm = host.Attach(new RecoveryViewModel(host));
        Assert.False(vm.ApproveCommand.CanExecute(null)); host.Execute(vm.ReviewCommand);
        Assert.True(vm.ApproveCommand.CanExecute(null)); var reviewed = host.LastReview!.ReviewId;
        if (boundary == "session") host.Publish(host.State with { Session = host.State.Session with { Id = Guid.NewGuid() } });
        else if (boundary == "generation") host.Publish(host.State with { Lease = new() { Mode = LeaseMode.RecoveryRequired, Generation = 8 } });
        else { host.Context = boundary == "disconnect" ? host.Context with { Connected = false } : host.Context with { Closing = true }; host.Publish(host.State); }
        Assert.False(vm.ApproveCommand.CanExecute(null)); vm.ApproveCommand.Execute(null); Assert.Empty(host.Approvals);
        host.Context = host.Context with { Connected = true, Closing = false }; host.Publish(host.State);
        Assert.False(vm.ApproveCommand.CanExecute(null)); host.Execute(vm.ReviewCommand); host.Execute(vm.ApproveCommand);
        Assert.NotEqual(reviewed, Assert.Single(host.Approvals).ReviewId);
    }

    [Fact]
    public async Task Late_recovery_review_cannot_authorize_a_new_session()
    {
        var host = new ManagementHostFake(); host.BeginRecovery(); var vm = host.Attach(new RecoveryViewModel(host));
        host.ReviewGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ReviewCommand.Execute(null); Assert.True(host.Context.Busy);
        host.Publish(host.State with { Session = host.State.Session with { Id = Guid.NewGuid() } });
        host.ReviewGate.SetResult(); await host.Done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.ApproveCommand.CanExecute(null)); Assert.DoesNotContain("확인 시각", vm.ReviewText);
    }
}

internal sealed class ManagementHostFake : IDeviceSettingsHost, IAccountManagementHost, IJobManagementHost, IRecoveryHost
{
    public StateView State { get; private set; }
    public FeatureContext Context { get; set; }
    private event Action<FeatureContext>? Changed;
    public Exception? Error { get; private set; }
    public bool Fail { get; set; }
    public TaskCompletionSource? ReviewGate { get; set; }
    public TaskCompletionSource Done { get; private set; } = new();
    public RecoveryReview? LastReview { get; private set; }
    public List<DeviceRequest> DeviceSaves { get; } = [];
    public List<RoleRequest> RoleSaves { get; } = [];
    public List<UnassignRoleRequest> Unassignments { get; } = [];
    public List<CreateAccountRequest> Creations { get; } = [];
    public List<UpdateAccountRequest> PermissionChanges { get; } = [];
    public List<SubmitRequest> Submissions { get; } = [];
    public List<JobActionRequest> Cancellations { get; } = [];
    public List<JobActionRequest> Switches { get; } = [];
    public List<JobActionRequest> DisplayStops { get; } = [];
    public List<JobActionRequest> EditCancellations { get; } = [];
    public List<RecoveryApprovalRequest> Approvals { get; } = [];
    public ManagementHostFake()
    {
        State = new FeatureHostFake().State;
        State = State with { RoleUnassignmentSupported = true, RoleManagementSupported = true,
            Accounts = [new(State.Session.UserId, "admin", AccountRole.Administrator, true, true, [])] };
        Context = new(State, Connected: true, CanControl: true, CanConfigure: true);
    }
    public T Attach<T>(T vm) where T : FeatureViewModel { Changed += vm.UpdateContext; vm.UpdateContext(Context); return vm; }
    public void Publish(StateView state) { State = state; Context = Context with { State = state }; Changed?.Invoke(Context); }
    public void BeginRecovery() => Publish(State with { Lease = new() { Mode = LeaseMode.RecoveryRequired, Generation = 7 } });
    public async Task RunAsync(Func<Task> action)
    {
        Done = new(TaskCreationOptions.RunContinuationsAsynchronously); Error = null;
        Context = Context with { Busy = true }; Publish(State);
        try { await action(); }
        catch (Exception e) { Error = e; }
        finally { Context = Context with { Busy = false }; Publish(State); Done.TrySetResult(); }
    }
    public void Execute(AsyncCommand command) { Assert.True(command.CanExecute(null)); command.Execute(null); Assert.Null(Error); Assert.False(Context.Busy); }
    public void ReportStatus(string message) { }
    public Task RefreshAsync() { Publish(State); return Task.CompletedTask; }
    public Task SubmitAsync(SubmitRequest request) { Submissions.Add(request); return Task.CompletedTask; }
    public Task<DeviceState> ReconcileAsync(ReconcileRequest request) => Task.FromResult(State.DeviceStates[request.DeviceId]);
    public Task<DeviceConfig> SaveDeviceDiagnosticsAsync(DeviceDiagnosticsRequest request) => Task.FromResult(State.Devices.Single(d => d.Id == request.DeviceId));
    public Task<PcRegistration> RegisterSessionPcAsync(RegisterSessionPcRequest request) => Task.FromResult(new PcRegistration(State.Session.PcId, State.Session.PcName));
    public Task<DeviceConfig> SaveDeviceAsync(DeviceRequest request)
    {
        DeviceSaves.Add(request); if (Fail) throw new InvalidOperationException("fixture save failure");
        var saved = new DeviceConfig(request.Id, request.PcId, request.PcName, request.Name, request.ConnectionId,
            request.ModelId, request.ExpectedVersion + 1, request.Enabled, request.Fault, request.LatencyMs)
            { DriverId = request.DriverId!, Connection = request.Connection!, DriverOptions = request.DriverOptions! };
        return Task.FromResult(saved);
    }
    public Task<RoleBinding> SaveRoleAsync(RoleRequest request)
    {
        RoleSaves.Add(request); var saved = new RoleBinding(request.Id, request.DeviceId, request.ExpectedVersion + 1);
        Publish(State with { Roles = [.. State.Roles.Where(r => r.Id != saved.Id), saved] }); return Task.FromResult(saved);
    }
    public Task UnassignRoleAsync(UnassignRoleRequest request)
    {
        Unassignments.Add(request); Publish(State with { Roles = State.Roles.Where(r => r.Id != request.Id).ToArray() }); return Task.CompletedTask;
    }
    public Task<RoleBinding> RenameRoleAsync(RenameRoleRequest request)
    {
        var saved = request.ExpectedRole with { Name = request.Name.Trim(), IsDefault = false };
        Publish(request.ExpectedAssigned
            ? State with { Roles = State.Roles.Select(r => r.Id == saved.Id ? saved : r).ToArray() }
            : State with { UnassignedRoles = State.UnassignedRoles.Select(r => r.Id == saved.Id ? saved : r).ToArray() });
        return Task.FromResult(saved);
    }
    public Task DeleteRoleAsync(DeleteRoleRequest request)
    {
        Publish(State with { Roles = State.Roles.Where(r => r.Id != request.ExpectedRole.Id).ToArray(),
            UnassignedRoles = State.UnassignedRoles.Where(r => r.Id != request.ExpectedRole.Id).ToArray() });
        return Task.CompletedTask;
    }
    public Task CreateAccountAsync(CreateAccountRequest request)
    { Creations.Add(request); if (Fail) throw new InvalidOperationException("fixture account failure"); return Task.CompletedTask; }
    public Task UpdateAccountAsync(UpdateAccountRequest request) { PermissionChanges.Add(request); return Task.CompletedTask; }
    public Task CancelJobAsync(JobActionRequest request) { Cancellations.Add(request); return Task.CompletedTask; }
    public Task SwitchToManualAsync(JobActionRequest request) { Switches.Add(request); return Task.CompletedTask; }
    public Task StopDisplayAsync(JobActionRequest request) { DisplayStops.Add(request); return Task.CompletedTask; }
    public Task<HiperwallEditReceipt> CancelEditAsync(JobActionRequest request)
    { EditCancellations.Add(request); return Task.FromResult(State.OutstandingHiperwallEdits.Single(r => r.Request.RequestId == request.JobId)); }
    public async Task<RecoveryReview> ReviewAsync()
    {
        var review = new RecoveryReview(Guid.NewGuid(), State.Lease.Generation, DateTimeOffset.UtcNow, State.Jobs, []);
        if (ReviewGate is not null) await ReviewGate.Task;
        LastReview = review; return review;
    }
    public Task ApproveRecoveryAsync(RecoveryApprovalRequest request) { Approvals.Add(request); return Task.CompletedTask; }
    public Job NewJob() => new() { RequestFingerprint = "fixture", Kind = JobKind.Scenario,
        Snapshot = new(State.SiteId, "Virtual", Guid.NewGuid(), State.Session.UserId, State.Session.UserName, Guid.NewGuid(),
            State.Session.PcId, State.Session.PcName, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid(), 1, "original work", []) };
}
