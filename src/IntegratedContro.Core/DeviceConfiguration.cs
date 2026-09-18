using System.Globalization;

namespace IntegratedContro.Core;

public enum ConnectionSaveMode { Existing, Create, UpdateShared }
public enum DeviceSettingTarget { Endpoint, Address, ConnectionOption, DriverOption }
public enum DeviceSettingKind { Text, Integer, Boolean, Choice }

/// <summary>Non-secret fields declared by a driver. Fixed fields are managed by the application.</summary>
public sealed record DeviceSettingDefinition(string Key, string Label, DeviceSettingTarget Target,
    DeviceSettingKind Kind = DeviceSettingKind.Text, string? DefaultValue = null,
    bool Required = false, int? Minimum = null, int? Maximum = null)
{
    public string? TransportId { get; init; }
    public string[] Choices { get; init; } = [];
    public string? FixedValue { get; init; }
    public bool IsShared => Target is DeviceSettingTarget.Endpoint or DeviceSettingTarget.ConnectionOption;
    public bool Accepts(string value) =>
        (value.Length == 0 && !Required && FixedValue is null) ||
        ((FixedValue is null || FixedValue == value) && value.Length <= 512 && !value.Any(char.IsControl) && (Kind switch
        {
            DeviceSettingKind.Integer => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                (Minimum is null || number >= Minimum) && (Maximum is null || number <= Maximum),
            DeviceSettingKind.Boolean => value is "true" or "false",
            DeviceSettingKind.Choice => Choices.Contains(value),
            _ => !Required || !string.IsNullOrWhiteSpace(value)
        }));
}
public sealed record PcRegistration(Guid Id, string Name);
public sealed record SharedDeviceConnection(string Id, string Name, int Version, DeviceConnection Settings)
{
    public string Label => $"{Name} · {Settings.TransportId} · {(Settings.Endpoint.Length == 0 ? "주소 없음" : Settings.Endpoint)}";
    public string Describe(IEnumerable<PcRegistration> pcs, IEnumerable<DeviceModel> models)
    {
        var parts = new List<string> { Label };
        if (Settings.ExecutionPcId is { } pc)
            parts.Add($"통신 실행 PC: {pcs.FirstOrDefault(p => p.Id == pc)?.Name ?? "등록 정보 없음"}");
        foreach (var option in Settings.Options)
        {
            var field = models.SelectMany(m => m.Settings).FirstOrDefault(f => f.Target == DeviceSettingTarget.ConnectionOption &&
                f.Key == option.Key && (f.TransportId is null || f.TransportId == Settings.TransportId));
            parts.Add($"{field?.Label ?? option.Key}: {option.Value}");
        }
        return string.Join(" · ", parts);
    }
}

/// <summary>Migration uses explicit IDs and exact settings, never names or transports alone.</summary>
public static class DeviceConfigurationStorage
{
    public static void Upgrade(HostState state)
    {
        foreach (var pc in state.Devices.Where(d => d.PcId != Guid.Empty).GroupBy(d => d.PcId))
            if (state.Pcs.All(p => p.Id != pc.Key)) state.Pcs.Add(new(pc.Key, pc.First().PcName));
        foreach (var group in state.Roles.GroupBy(r => r.DeviceId).ToArray())
        {
            var name = state.Devices.FirstOrDefault(d => d.Id == group.Key)?.Name ?? "기존 장비";
            var index = 0;
            foreach (var role in group)
            {
                index++;
                if (!role.IsDefault && string.IsNullOrWhiteSpace(role.Name))
                    state.Roles[state.Roles.IndexOf(role)] = role with { Name = $"{name} 역할 {index}" };
            }
        }
        foreach (var deleted in state.DeletedRoleVersions.Where(p => !state.RemovedRoleIds.Contains(p.Key) &&
            state.Roles.All(r => r.Id != p.Key) && state.UnassignedRoles.All(r => r.Id != p.Key)))
        {
            var scenario = state.Scenarios.FirstOrDefault(s => s.Steps.Any(step => step.RoleId == deleted.Key));
            state.UnassignedRoles.Add(new(deleted.Key, Guid.Empty, deleted.Value)
            { Name = $"기존 역할 {state.UnassignedRoles.Count + 1}" + (scenario is null ? "" : $" · {scenario.Name}") });
        }
        if (state.DeviceConfigurationVersion >= 1) return;
        foreach (var d in state.Devices.ToArray())
        {
            var settings = SharedSettings(d.Connection);
            var connection = state.DeviceConnections.FirstOrDefault(c => c.Id == d.ConnectionId && c.Settings.HasSameSettings(settings));
            if (connection is null)
            {
                var id = !string.IsNullOrWhiteSpace(d.ConnectionId) && state.DeviceConnections.All(c => c.Id != d.ConnectionId)
                    ? d.ConnectionId : Guid.NewGuid().ToString("N");
                connection = new(id, $"{d.Name} 연결", 1, settings);
                state.DeviceConnections.Add(connection);
            }
            state.Devices[state.Devices.IndexOf(d)] = d with { ConnectionId = connection.Id, LegacyConnectionId = d.ConnectionId };
        }
        state.DeviceConfigurationVersion = 1;
    }
    public static DeviceConnection SharedSettings(DeviceConnection value) => value with { Address = "", Options = new(value.Options) };
    public static DeviceConfig Materialize(DeviceConfig device, IEnumerable<SharedDeviceConnection> connections)
    {
        var shared = connections.SingleOrDefault(c => c.Id == device.ConnectionId);
        return shared is null ? device : device with
        { Connection = shared.Settings with { Address = device.Connection.Address, Options = new(shared.Settings.Options) } };
    }
    public static void Materialize(HostState state)
    {
        Upgrade(state);
        state.Devices = state.Devices.Select(d => Materialize(d, state.DeviceConnections)).ToList();
    }
    public static HostState ForStorage(HostState state)
    {
        var copy = JsonDefaults.Copy(state);
        Upgrade(copy);
        // Only device addressing is stored on registrations. Accepted snapshots intentionally retain complete settings.
        copy.Devices = copy.Devices.Select(d => d with { Connection = new("", "", d.Connection.Address) }).ToList();
        return copy;
    }
}
