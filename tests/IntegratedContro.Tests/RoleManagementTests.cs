using IntegratedContro.App;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class RoleManagementTests
{
    [Fact]
    public async Task Rename_preserves_scenario_and_accepted_execution_and_rejects_stale_edits()
    {
        using var r = new Rig(); r.Device();
        var role = r.Service.GetState(r.Admin.Token).Roles.Single();
        var definition = r.Scenario(new ScenarioStep(role.Id, DeviceOperation.Power, 1));
        var job = r.SubmitScenario(definition);
        var changed = r.Service.RenameRole(r.Admin.Token, new(r.Generation, role, true, "  입구 조명  "));
        Assert.Equal("입구 조명", changed.Name); Assert.Equal(role.Version, changed.Version);
        Assert.Equal(role, r.Job(job.Id).Snapshot.Steps.Single().Role);
        Assert.Equal(definition.Steps, r.Service.GetState(r.Admin.Token).Scenarios.Single().Steps);
        Rig.Reject("version_conflict", () => r.Service.RenameRole(r.Admin.Token, new(r.Generation, role, true, "덮어쓰기")));
        Rig.Reject("version_conflict", () => r.Service.DeleteRole(r.Admin.Token, new(r.Generation, role, true)));
        await r.Service.DispatchNextAsync();
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status); Assert.Single(r.Driver.Sent);
        r.Service.Release(r.Admin.Token, r.Generation); r.Restart();
        Assert.Equal(changed, r.Service.GetState(r.Admin.Token).Roles.Single());
    }

    [Fact]
    public async Task Deletion_requires_unassignment_and_preserves_devices_history_and_removal_after_restart()
    {
        using var r = new Rig(); var device = r.Device();
        var original = r.Service.GetState(r.Admin.Token).Roles.Single();
        var job = r.Service.Submit(r.Admin.Token, r.Manual()); await r.Service.DispatchNextAsync();
        var before = r.Service.GetState(r.Admin.Token);
        Rig.Reject("role_assigned", () => r.Service.DeleteRole(r.Admin.Token, new(r.Generation, original, true)));
        Rig.Reject("version_conflict", () => r.Service.DeleteRole(r.Admin.Token, new(r.Generation, original, false)));
        r.Service.UnassignRole(r.Admin.Token, new(r.Generation, original.Id, device.Id, original.Version));
        var renamed = r.Service.RenameRole(r.Admin.Token, new(r.Generation, original, false, "미배정 조명"));
        Rig.Reject("version_conflict", () => r.Service.DeleteRole(r.Admin.Token, new(r.Generation, original, false)));
        Assert.True(r.Service.DeleteRole(r.Admin.Token, new(r.Generation, renamed, false)));
        var after = r.Service.GetState(r.Admin.Token);
        Assert.Empty(after.Roles); Assert.Empty(after.UnassignedRoles);
        Assert.Equal(before.Devices, after.Devices);
        Assert.Equal(before.DeviceStates[device.Id].Values, after.DeviceStates[device.Id].Values);
        Assert.Equal(original, r.Job(job.Id).Snapshot.Steps.Single().Role);
        Assert.Equal(JobStatus.Completed, r.Job(job.Id).Status);
        Assert.Single(after.Audit, a => a.Action == "RoleDeleted");
        r.Service.Release(r.Admin.Token, r.Generation); r.Restart();
        r.Generation = r.Service.Acquire(r.Admin.Token).Generation;
        Assert.Empty(r.Service.GetState(r.Admin.Token).UnassignedRoles);
        Rig.Reject("role_deleted", () => r.Service.SaveRole(r.Admin.Token, new(r.Generation, original.Id, device.Id)));
        Assert.NotEqual(original.Id, r.Service.SaveRole(r.Admin.Token, new(r.Generation, "", device.Id) { Name = renamed.Name }).Id);
    }

    [Fact]
    public void Referenced_unassigned_role_requires_definition_cleanup_before_deletion()
    {
        using var r = new Rig(); var device = r.Device();
        var original = r.Service.GetState(r.Admin.Token).Roles.Single();
        var scenario = r.Scenario(new ScenarioStep(original.Id, DeviceOperation.Power, 1));
        r.Service.UnassignRole(r.Admin.Token, new(r.Generation, original.Id, device.Id, original.Version));
        var request = new DeleteRoleRequest(r.Generation, original, false);
        Rig.Reject("role_referenced", () => r.Service.DeleteRole(r.Admin.Token, request));
        Assert.Single(r.Service.GetState(r.Admin.Token).UnassignedRoles);
        r.Service.DeleteScenario(r.Admin.Token, new(r.Generation, scenario.Id, scenario.Version));
        Assert.True(r.Service.DeleteRole(r.Admin.Token, request));
    }

    [Fact]
    public void Management_requires_current_admin_owner_and_rejects_changed_assignment()
    {
        using var r = new Rig(); var device = r.Device(); var op = r.Operator("operator");
        var role = r.Service.GetState(r.Admin.Token).Roles.Single();
        var rename = new RenameRoleRequest(r.Generation, role, true, "변경");
        var delete = new DeleteRoleRequest(r.Generation, role, true);
        Rig.Reject("unauthorized", () => r.Service.RenameRole("missing", rename));
        Rig.Reject("unauthorized", () => r.Service.DeleteRole("missing", delete));
        Rig.Reject("lease_required", () => r.Service.RenameRole(r.Admin.Token, rename with { Generation = r.Generation - 1 }));
        Rig.Reject("lease_required", () => r.Service.DeleteRole(r.Admin.Token, delete with { Generation = r.Generation - 1 }));
        r.Service.UnassignRole(r.Admin.Token, new(r.Generation, role.Id, device.Id, role.Version));
        Rig.Reject("version_conflict", () => r.Service.RenameRole(r.Admin.Token, rename));
        Rig.Reject("version_conflict", () => r.Service.DeleteRole(r.Admin.Token, delete));
        r.Service.Release(r.Admin.Token, r.Generation); var lease = r.Service.Acquire(op.Token);
        Rig.Reject("admin_required", () => r.Service.RenameRole(op.Token, rename with { Generation = lease.Generation, ExpectedAssigned = false }));
        Rig.Reject("admin_required", () => r.Service.DeleteRole(op.Token, delete with { Generation = lease.Generation, ExpectedAssigned = false }));
    }

    [Fact]
    public void Editor_keeps_draft_across_polling_and_requires_reload_after_external_changes()
    {
        var host = new ManagementHostFake(); var vm = host.Attach(new DeviceSettingsViewModel(host, Guid.NewGuid()));
        vm.ManagedRole = vm.RoleChoices.First(); var original = vm.ManagedRole.Binding;
        vm.RoleDisplayName = "입력 중";
        host.Publish(JsonDefaults.Copy(host.State));
        Assert.Equal("입력 중", vm.RoleDisplayName); Assert.True(vm.RenameRoleCommand.CanExecute(null));
        Assert.False(vm.DeleteRoleCommand.CanExecute(null));
        var changed = original with { Name = "다른 수정" };
        host.Publish(host.State with { Roles = host.State.Roles.Select(r => r.Id == changed.Id ? changed : r).ToArray() });
        Assert.Equal("입력 중", vm.RoleDisplayName); Assert.False(vm.RenameRoleCommand.CanExecute(null));
        host.Execute(vm.ReloadRoleCommand); Assert.Equal(changed.Name, vm.RoleDisplayName);
        host.Publish(host.State with { Roles = host.State.Roles.Where(r => r.Id != changed.Id).ToArray(), UnassignedRoles = [changed] });
        Assert.False(vm.DeleteRoleCommand.CanExecute(null));
        host.Execute(vm.ReloadRoleCommand); Assert.True(vm.DeleteRoleCommand.CanExecute(null));
        host.Publish(host.State with { RoleManagementSupported = false });
        Assert.False(vm.DeleteRoleCommand.CanExecute(null)); Assert.False(vm.RenameRoleCommand.CanExecute(null));
        host.Publish(host.State with { RoleManagementSupported = true });
        host.Execute(vm.DeleteRoleCommand); Assert.Null(vm.ManagedRole); Assert.Equal("", vm.RoleDisplayName);
    }
}
