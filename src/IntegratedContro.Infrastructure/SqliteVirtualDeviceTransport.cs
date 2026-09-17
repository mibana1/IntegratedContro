using IntegratedContro.Core;
using Microsoft.Data.Sqlite;

namespace IntegratedContro.Infrastructure;

public sealed class SqliteVirtualDeviceTransport(string connectionString) : IVirtualDeviceTransport
{
    public async Task WriteAsync(DeviceConfig target, DeviceOperation operation, int value, CancellationToken ct)
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO virtual_values(pc_id,device_id,operation,value) VALUES($pc,$device,$op,$value)
            ON CONFLICT(pc_id,device_id,operation) DO UPDATE SET value=excluded.value;
            """;
        command.Parameters.AddWithValue("$pc", target.PcId.ToString());
        command.Parameters.AddWithValue("$device", target.Id.ToString());
        command.Parameters.AddWithValue("$op", operation.ToString());
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<DeviceOperation, int>> ReadAsync(DeviceConfig target, CancellationToken ct)
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation,value FROM virtual_values WHERE pc_id=$pc AND device_id=$device;";
        command.Parameters.AddWithValue("$pc", target.PcId.ToString());
        command.Parameters.AddWithValue("$device", target.Id.ToString());
        using var reader = await command.ExecuteReaderAsync(ct);
        var values = new Dictionary<DeviceOperation, int>();
        while (await reader.ReadAsync(ct))
            if (Enum.TryParse<DeviceOperation>(reader.GetString(0), out var operation)) values[operation] = reader.GetInt32(1);
        return values;
    }
}
