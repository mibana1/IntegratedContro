using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    private DeviceModel? _selectedModel;
    public DeviceModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            var previous = _selectedModel?.Id;
            _selectedModel = value;
            if (value is not null && previous != value.Id && !DeviceTransports.Contains(DeviceTransportId))
                DeviceTransportId = DeviceTransports.FirstOrDefault() ?? "";
            Changed(nameof(SelectedModel)); Changed(nameof(DeviceDriverId));
            Changed(nameof(DeviceTransports)); Changed(nameof(DeviceIsSimulation));
            if (previous != value?.Id) Changed(nameof(DeviceTransportId));
        }
    }
    public string DeviceModeSummary => _state is null || _state.Devices.Length == 0 ? "등록된 장비 없음" :
        _state.Devices.All(d => _state.Models.Any(m => m.Id == d.ModelId && m.IsSimulation)) ? "장비: 가상" :
        _state.Devices.All(d => _state.Models.Any(m => m.Id == d.ModelId && !m.IsSimulation)) ? "장비: 실장비 드라이버" : "장비: 혼합 / 드라이버 설정 확인";
    public string DeviceDriverId => SelectedModel?.DriverId ?? "";
    public IEnumerable<string> DeviceTransports => SelectedModel?.TransportIds ?? [];
    public bool DeviceIsSimulation => SelectedModel?.IsSimulation == true;
    private string _deviceTransportId = "virtual";
    public string DeviceTransportId
    {
        get => _deviceTransportId;
        set
        {
            // ComboBox clears SelectedItem while replacing its catalog. Keep the draft selection.
            if (!string.IsNullOrWhiteSpace(value)) Set(ref _deviceTransportId, value);
        }
    }
    public string DeviceEndpoint { get; set; } = "";
    public string DeviceAddress { get; set; } = "";
    public string DeviceTransportOptions { get; set; } = "{}";
    public string DeviceDriverOptions { get; set; } = "{}";

    private static Dictionary<string, string> ParseDeviceOptions(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text, JsonDefaults.Options)
                ?? throw new ArgumentException("옵션은 JSON 객체로 입력하세요.");
        }
        catch (JsonException) { throw new ArgumentException("옵션은 이름과 문자열 값으로 된 JSON 객체로 입력하세요."); }
    }
    private void LoadDeviceSettings(DeviceConfig device)
    {
        DeviceTransportId = device.Connection.TransportId; DeviceEndpoint = device.Connection.Endpoint;
        DeviceAddress = device.Connection.Address;
        DeviceTransportOptions = JsonSerializer.Serialize(device.Connection.Options, JsonDefaults.Options);
        DeviceDriverOptions = JsonSerializer.Serialize(device.DriverOptions, JsonDefaults.Options);
        NotifyDeviceSettings();
    }
    private void NotifyDeviceSettings()
    {
        foreach (var name in new[] { nameof(SelectedModel), nameof(DeviceDriverId), nameof(DeviceTransports),
            nameof(DeviceIsSimulation), nameof(DeviceTransportId), nameof(DeviceEndpoint), nameof(DeviceAddress),
            nameof(DeviceTransportOptions), nameof(DeviceDriverOptions) }) Changed(name);
    }
}
