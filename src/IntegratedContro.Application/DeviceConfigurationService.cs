using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

internal sealed partial class DeviceExecutionService
{
    public DeviceConfig SaveDevice(string token, DeviceRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        Text(request.Name, "장비 이름");
        var model = _drivers.Model(request.ModelId);
        var requestedConnectionId = request.ConnectionId?.Trim() ?? "";
        var modern = request.ConnectionMode is not null;
        if (!modern && !model.IsSimulation)
            Require(request.Fault == VirtualFault.None && request.LatencyMs == 0, "simulation_required", "실장비에는 가상 장애·지연을 설정할 수 없습니다.", 400);
        Require(!modern || Enum.IsDefined(request.ConnectionMode!.Value), "invalid_connection", "연결 저장 방식을 선택하세요.", 400);
        var id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id;
        var old = s.Devices.SingleOrDefault(d => d.Id == id);
        Require((old?.Version ?? 0) == request.ExpectedVersion, "version_conflict", "장비 설정을 다시 조회하세요.");
        var pcId = modern && !model.RequiresTargetPc ? old?.PcId ?? Guid.Empty : request.PcId;
        var pcName = modern && !model.RequiresTargetPc ? old?.PcName ?? "" : (request.PcName ?? "").Trim();
        if (model.RequiresTargetPc)
        {
            var pc = s.Pcs.SingleOrDefault(p => p.Id == pcId);
            Require(pc is not null, "target_required", "등록된 대상 PC를 선택하세요. 다른 PC로 자동 대체하지 않습니다.", 400);
            pcName = pc!.Name;
        }
        else if (!modern && pcId != Guid.Empty && s.Pcs.All(p => p.Id != pcId))
            s.Pcs.Add(new(pcId, pcName)); // Retain legacy virtual-value keys and PC identity.
        var candidate = new DeviceConfig(id, pcId, pcName, request.Name.Trim(), requestedConnectionId,
            request.ModelId, (old?.Version ?? 0) + 1, request.Enabled,
            modern ? old?.Fault ?? VirtualFault.None : request.Fault,
            modern ? old?.LatencyMs ?? (model.IsSimulation ? 50 : 0) : request.LatencyMs)
        {
            DriverId = request.DriverId ?? model.DriverId,
            Connection = JsonDefaults.Copy(request.Connection ?? old?.Connection ?? new DeviceConnection()),
            DriverOptions = JsonDefaults.Copy(request.DriverOptions ?? old?.DriverOptions ?? new Dictionary<string, string>()),
            Location = request.Location?.Trim() ?? old?.Location ?? "",
            LegacyConnectionId = old?.LegacyConnectionId
        };
        Require(candidate.Location.Length <= 120 && !candidate.Location.Any(char.IsControl), "invalid_location", "위치는 120자 이내로 입력하세요.", 400);
        Require(candidate.Connection.Options is not null && candidate.Connection.Endpoint is not null && candidate.Connection.Address is not null,
            "invalid_connection", "연결 설정의 주소·옵션을 확인하세요.", 400);
        candidate = ApplySettingDefaults(candidate, model);
        var shared = s.Connections.SingleOrDefault(c => c.Id == requestedConnectionId);
        var mode = request.ConnectionMode;
        if (mode == ConnectionSaveMode.Existing || mode == ConnectionSaveMode.UpdateShared)
        {
            Require(shared is not null && shared.Version == request.ExpectedConnectionVersion,
                "connection_changed", "공유 연결이 변경되었습니다. 연결 설정을 다시 불러오세요.");
            if (mode == ConnectionSaveMode.Existing)
                candidate = candidate with { Connection = shared!.Settings with { Address = candidate.Connection.Address } };
        }
        if (mode == ConnectionSaveMode.Create)
        {
            Text(request.ConnectionName, "연결 이름");
            shared = new(Guid.NewGuid().ToString("N"), request.ConnectionName.Trim(), 1, DeviceConfigurationStorage.SharedSettings(candidate.Connection));
            s.Connections.Add(shared);
        }
        else if (mode == ConnectionSaveMode.UpdateShared)
        {
            Text(request.ConnectionName, "연결 이름");
            var updated = shared! with { Name = request.ConnectionName.Trim(), Version = shared.Version + 1,
                Settings = DeviceConfigurationStorage.SharedSettings(candidate.Connection) };
            s.Connections[s.Connections.IndexOf(shared)] = updated;
            shared = updated;
        }
        else if (!modern)
        {
            // Compatibility for previous clients: an explicit ID may reference a connection, but a changed
            // per-device payload must never silently modify other registrations.
            Text(request.ConnectionId, "연결 ID");
            var settings = DeviceConfigurationStorage.SharedSettings(candidate.Connection);
            if (shared is null)
            {
                shared = new(requestedConnectionId, $"{candidate.Name} 연결", 1, settings);
                s.Connections.Add(shared);
            }
            else if (!shared.Settings.HasSameSettings(settings))
            {
                if (s.Devices.Any(d => d.Id != id && d.ConnectionId == shared.Id))
                {
                    shared = new(Guid.NewGuid().ToString("N"), $"{candidate.Name} 연결", 1, settings);
                    s.Connections.Add(shared);
                }
                else
                {
                    var updated = shared with { Settings = settings, Version = shared.Version + 1 };
                    s.Connections[s.Connections.IndexOf(shared)] = updated; shared = updated;
                }
            }
        }
        Require(shared is not null, "connection_required", "기존 연결을 선택하거나 새 연결을 만드세요.", 400);
        candidate = candidate with { ConnectionId = shared!.Id,
            Connection = shared.Settings with { Address = candidate.Connection.Address, Options = new(shared.Settings.Options) } };
        var affected = s.Devices.Where(d => d.ConnectionId == shared.Id && d.Id != id).ToArray();
        foreach (var other in affected)
        {
            var updated = DeviceConfigurationStorage.Materialize(other, s.Connections);
            ValidateConfiguration(s, updated);
            if (!other.HasSameExecutionSettings(updated)) StoreDevice(s, updated with { Version = other.Version + 1 }, other);
        }
        ValidateConfiguration(s, candidate);
        candidate = StoreDevice(s, candidate, old);
        if (old is null && modern)
            s.Roles.Add(new(Guid.NewGuid().ToString("N"), candidate.Id, 1) { IsDefault = true });
        foreach (var device in affected.Append(candidate).Where(d => d.Enabled))
            foreach (var role in s.Roles.Where(r => r.DeviceId == device.Id))
                foreach (var step in s.Scenarios.SelectMany(x => x.Steps).Where(x => x.RoleId == role.Id))
                    Resolve(s, _host.User(s, session), step);
        _host.Audit(s, session.Info.UserId, "DeviceSaved", $"pc={candidate.PcId}; device={candidate.Id}; v={candidate.Version}");
        if (mode == ConnectionSaveMode.UpdateShared)
            _host.Audit(s, session.Info.UserId, "SharedConnectionSaved", $"connection={shared.Id}; affected={string.Join(",", affected.Select(d => d.Id).Append(id))}");
        return candidate;
    });

    private void ValidateConfiguration(DeviceStateScope state, DeviceConfig device)
    {
        if (device.Connection.ExecutionPcId is { } pc)
            Require(state.Pcs.Any(p => p.Id == pc), "execution_pc_missing", "등록된 통신 실행 PC를 선택하세요.", 400);
        _drivers.Resolve(device);
    }
    private static DeviceConfig StoreDevice(DeviceStateScope state, DeviceConfig candidate, DeviceConfig? old)
    {
        var changed = old is null || !old.HasSameExecutionSettings(candidate);
        candidate = candidate with { ExecutionVersion = old is null ? candidate.Version :
            changed ? old.ExecutionVersion + 1 : old.ExecutionVersion };
        state.Devices.RemoveAll(d => d.Id == candidate.Id); state.Devices.Add(candidate);
        if (changed) state.DeviceStates[candidate.Id] = new() { Connection = "장비 설정 저장 / 새 상태 조회 필요" };
        return candidate;
    }
    private static DeviceConfig ApplySettingDefaults(DeviceConfig device, DeviceModel model)
    {
        var connection = device.Connection with { Options = new(device.Connection.Options) };
        var options = new Dictionary<string, string>(device.DriverOptions);
        foreach (var field in model.Settings.Where(f => f.TransportId is null || f.TransportId == connection.TransportId))
        {
            var value = field.FixedValue ?? field.DefaultValue;
            if (value is null) continue;
            switch (field.Target)
            {
                case DeviceSettingTarget.Endpoint:
                    if (field.FixedValue is not null || connection.Endpoint.Length == 0) connection = connection with { Endpoint = value }; break;
                case DeviceSettingTarget.Address:
                    if (field.FixedValue is not null || connection.Address.Length == 0) connection = connection with { Address = value }; break;
                case DeviceSettingTarget.ConnectionOption:
                    if (field.FixedValue is not null || !connection.Options.ContainsKey(field.Key)) connection.Options[field.Key] = value; break;
                case DeviceSettingTarget.DriverOption:
                    if (field.FixedValue is not null || !options.ContainsKey(field.Key)) options[field.Key] = value; break;
            }
        }
        return device with { Connection = connection, DriverOptions = options };
    }
    public DeviceConfig SaveDiagnostics(string token, DeviceDiagnosticsRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        var old = s.Devices.SingleOrDefault(d => d.Id == request.DeviceId);
        Require(old is not null && old.Version == request.ExpectedVersion, "version_conflict", "진단할 장비를 다시 불러오세요.");
        Require(_drivers.Model(old!.ModelId).IsSimulation, "simulation_required", "가상 장비에만 장애·지연을 설정할 수 있습니다.", 400);
        Require(Enum.IsDefined(request.Fault) && request.LatencyMs is >= 0 and <= 30000, "invalid_diagnostics", "가상 지연은 0~30000ms입니다.", 400);
        var updated = old with { Fault = request.Fault, LatencyMs = request.LatencyMs, Version = old.Version + 1 };
        ValidateConfiguration(s, updated);
        updated = StoreDevice(s, updated, old);
        _host.Audit(s, session.Info.UserId, "DeviceDiagnosticsSaved", $"device={old.Id}; fault={updated.Fault}; latency={updated.LatencyMs}");
        return updated;
    });
    public PcRegistration RegisterSessionPc(string token, RegisterSessionPcRequest request) => _host.Change(s =>
    {
        var session = _host.Owner(s, token, request.Generation); _host.Admin(s, token);
        var pc = new PcRegistration(session.Info.PcId, session.Info.PcName);
        s.Pcs.RemoveAll(p => p.Id == pc.Id); s.Pcs.Add(pc);
        // Display-name changes preserve identity and do not redirect any admitted operation.
        s.Devices = s.Devices.Select(d => d.PcId == pc.Id ? d with { PcName = pc.Name } : d).ToList();
        _host.Audit(s, session.Info.UserId, "PcRegistered", $"pc={pc.Id}");
        return pc;
    });
}
