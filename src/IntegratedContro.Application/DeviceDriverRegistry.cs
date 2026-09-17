using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

/// <summary>Explicit host registration. Unknown models and transports never fall back to another driver.</summary>
public sealed class DeviceDriverRegistry
{
    private readonly Dictionary<string, IDeviceDriver> _drivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceModel> _models = new(StringComparer.Ordinal);
    public DeviceModel[] Models => JsonDefaults.Copy(_models.Values.ToArray());

    public DeviceDriverRegistry(params IDeviceDriver[] drivers)
    {
        foreach (var driver in drivers)
        {
            if (string.IsNullOrWhiteSpace(driver.Id) || string.IsNullOrWhiteSpace(driver.Version) || !_drivers.TryAdd(driver.Id, driver))
                throw new ArgumentException("드라이버 ID는 비어 있거나 중복될 수 없습니다.");
            foreach (var model in driver.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id) || model.Capabilities.Length == 0 ||
                    model.Capabilities.Select(c => c.Operation).Distinct().Count() != model.Capabilities.Length ||
                    model.Capabilities.Any(c => !Enum.IsDefined(c.Operation) || c.Minimum > c.Maximum || string.IsNullOrWhiteSpace(c.Unit)) ||
                    model.TransportIds.Length == 0 || model.TransportIds.Any(string.IsNullOrWhiteSpace) ||
                    !_models.TryAdd(model.Id, JsonDefaults.Copy(model with { DriverId = driver.Id, DriverVersion = driver.Version })))
                    throw new ArgumentException("모델 ID·기능·통신 방식 선언이 잘못되었거나 중복되었습니다.");
            }
        }
    }

    public DeviceModel Model(string modelId) => !string.IsNullOrWhiteSpace(modelId) && _models.TryGetValue(modelId, out var model)
        ? JsonDefaults.Copy(model) : throw new DomainException("unsupported_model", "등록된 장비 모델을 선택하세요.", 400);

    public IDeviceDriver Resolve(DeviceConfig device)
    {
        var model = Model(device.ModelId);
        Require(model.DriverId == device.DriverId && _drivers.ContainsKey(device.DriverId),
            "unsupported_driver", "모델과 드라이버 등록이 일치하지 않습니다.", 400);
        Require(device.Connection is not null && model.TransportIds.Contains(device.Connection.TransportId),
            "unsupported_transport", "이 모델이 지원하는 통신 방식을 선택하세요.", 400);
        Require(device.Connection!.Endpoint is not null && device.Connection.Endpoint.Length <= 2048 &&
            !device.Connection.Endpoint.Any(char.IsControl) && device.Connection.Address is not null &&
            device.Connection.Address.Length <= 120 && !device.Connection.Address.Any(char.IsControl),
            "invalid_connection", "연결 주소·장비 주소를 확인하세요.", 400);
        ValidateOptions(device.Connection.Options);
        ValidateOptions(device.DriverOptions);
        var driver = _drivers[device.DriverId];
        driver.ValidateConfiguration(JsonDefaults.Copy(device));
        return driver;
    }

    private static void ValidateOptions(Dictionary<string, string>? options) => Require(options is not null &&
        options.Count <= 32 && options.All(p => !string.IsNullOrWhiteSpace(p.Key) && p.Key.Length <= 80 &&
            !p.Key.Any(char.IsControl) && p.Value is not null && p.Value.Length <= 512 && !p.Value.Any(char.IsControl)),
        "invalid_options", "설정 옵션은 최대 32개이며 유효한 이름과 값을 사용해야 합니다.", 400);

    public static bool SameDefinition(DeviceModel left, DeviceModel right) => left.Id == right.Id &&
        left.DriverId == right.DriverId && left.DriverVersion == right.DriverVersion && left.IsSimulation == right.IsSimulation &&
        left.Capabilities.Length == right.Capabilities.Length && left.Capabilities.All(right.Capabilities.Contains) &&
        left.TransportIds.Order().SequenceEqual(right.TransportIds.Order());
}
