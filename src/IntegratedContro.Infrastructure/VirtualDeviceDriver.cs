using IntegratedContro.Application;
using IntegratedContro.Core;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Infrastructure;

/// <summary>Only simulated I/O. No discovery, device fallback, OS or physical control exists here.</summary>
public sealed class VirtualDeviceDriver(string connectionString) : IDeviceDriver
{
    public DeviceModel[] Models =>
    [
        new("virtual-light", "가상 조명 (조광)", [new(DeviceOperation.Power,0,1,"on/off"), new(DeviceOperation.Brightness,0,100,"%")], DeviceCategory.Lighting),
        new("virtual-light-basic", "가상 조명 (전원만)", [new(DeviceOperation.Power,0,1,"on/off")], DeviceCategory.Lighting),
        new("virtual-projector", "가상 프로젝터", [new(DeviceOperation.Power,0,1,"on/off"), new(DeviceOperation.Input,1,4,"입력 번호")], DeviceCategory.Projection),
        new("virtual-audio", "가상 음향", [new(DeviceOperation.Volume,0,100,"%"), new(DeviceOperation.Mute,0,1,"on/off")], DeviceCategory.Audio),
        new("virtual-lift", "가상 승강", [new(DeviceOperation.Lift,-1,1,"-1=하강 / 0=정지 / 1=상승"), new(DeviceOperation.Stop,0,0,"STOP")], DeviceCategory.Lift)
    ];
    public async Task<DriverResult> ExecuteAsync(StepSnapshot step, CancellationToken cancellationToken)
    {
        if (step.Kind != ScenarioStepKind.DeviceCommand || step.Target is not { } target)
            throw new InvalidOperationException("장비 명령 snapshot이 필요합니다.");
        if (target.Fault == VirtualFault.Disconnected) return new(StepStatus.Failed, "가상 연결 끊김 / 전송 없음");
        if (target.Fault == VirtualFault.Failure) return new(StepStatus.Failed, "주입된 가상 실패 / 전송 없음");
        await Task.Delay(target.LatencyMs, cancellationToken);
        if (target.Fault == VirtualFault.NoResponse) await Task.Delay(Timeout.Infinite, cancellationToken);
        var operation = step.Operation == DeviceOperation.Stop ? DeviceOperation.Lift : step.Operation;
        using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO virtual_values(pc_id,device_id,operation,value) VALUES($pc,$device,$op,$value)
                ON CONFLICT(pc_id,device_id,operation) DO UPDATE SET value=excluded.value;
                """;
            command.Parameters.AddWithValue("$pc", target.PcId.ToString());
            command.Parameters.AddWithValue("$device", target.Id.ToString());
            command.Parameters.AddWithValue("$op", operation.ToString());
            command.Parameters.AddWithValue("$value", step.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (target.Fault == VirtualFault.ResponseLost) await Task.Delay(Timeout.Infinite, cancellationToken);
        return new(StepStatus.Simulated, "가상 장비 값 반영 / 실제 장비 관측 아님",
            new Dictionary<DeviceOperation, int> { [operation] = step.Value });
    }
    public async Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken cancellationToken)
    {
        if (device.Fault is VirtualFault.Disconnected or VirtualFault.NoResponse or VirtualFault.Failure)
            return new(false, new Dictionary<DeviceOperation, int>(), "가상 상태 조회 불가: 연결/주입 장애를 확인하세요.");
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation,value FROM virtual_values WHERE pc_id=$pc AND device_id=$device;";
        command.Parameters.AddWithValue("$pc", device.PcId.ToString());
        command.Parameters.AddWithValue("$device", device.Id.ToString());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = Models.Single(m => m.Id == device.ModelId).Capabilities
            .ToDictionary(c => c.Operation, c => c.Minimum);
        while (await reader.ReadAsync(cancellationToken))
            if (Enum.TryParse<DeviceOperation>(reader.GetString(0), out var op) && values.ContainsKey(op))
                values[op] = reader.GetInt32(1);
        return new(true, values, "가상 상태 조회 / 실제 장비 관측 아님");
    }
}
