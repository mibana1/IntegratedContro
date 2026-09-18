using System.Globalization;
using IntegratedContro.Core;

namespace IntegratedContro.App;

// Controls follow the driver's capabilities and ranges, never a model ID or device name.
public sealed class ScenarioSetting : Bindable
{
    public Capability Capability { get; }
    public DeviceOperation Operation => Capability.Operation;
    public string Label => DeviceLabels.Operation(Operation);
    public int Minimum => Capability.Minimum;
    public int Maximum => Capability.Maximum;
    public string Range => $"{Minimum}~{Maximum} {Capability.Unit}";
    public string ValueLabel => IsValid ? DeviceLabels.Format(Operation, Value, Capability.Unit) : $"허용 범위: {Range}";
    public bool IsRange => Operation is DeviceOperation.Brightness or DeviceOperation.Volume;
    public bool HasOptions => Options.Length > 0;
    public bool IsNumeric => !HasOptions && !IsRange;
    public ScenarioSettingOption[] Options { get; }
    private bool _included;
    public bool Included { get => _included; set { if (Set(ref _included, value)) NotifyValue(); } }
    private string _valueText;
    public string ValueText
    {
        get => _valueText;
        set { if (Set(ref _valueText, value)) { Included = true; NotifyValue(); } }
    }
    public int Value
    {
        get => int.TryParse(_valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : Minimum;
        set => ValueText = value.ToString(CultureInfo.InvariantCulture);
    }
    public bool IsValid => int.TryParse(_valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value >= Minimum && value <= Maximum;
    public ScenarioSetting(Capability capability)
    {
        Capability = capability; _valueText = Minimum.ToString(CultureInfo.InvariantCulture);
        var values = Operation switch
        {
            DeviceOperation.Power or DeviceOperation.Mute when Minimum == 0 && Maximum == 1 => new[] { 1, 0 },
            DeviceOperation.Lift when Minimum == -1 && Maximum == 1 => new[] { 1, 0, -1 },
            DeviceOperation.Stop => new[] { Minimum },
            DeviceOperation.Input when (long)Maximum - Minimum is >= 0 and <= 31 => Enumerable.Range(Minimum, Maximum - Minimum + 1).ToArray(),
            _ => []
        };
        Options = values.Select(value => new ScenarioSettingOption(this, value)).ToArray();
    }
    private void NotifyValue()
    {
        Changed(nameof(Value)); Changed(nameof(ValueLabel)); Changed(nameof(IsValid));
        foreach (var option in Options) option.NotifySelected();
    }
}
public sealed class ScenarioSettingOption : Bindable
{
    private readonly ScenarioSetting _owner;
    public int Value { get; }
    public string Label => _owner.Operation switch
    {
        DeviceOperation.Power => Value == 1 ? "ON · 켜기" : "OFF · 끄기",
        DeviceOperation.Mute => Value == 1 ? "음소거" : "소리 켜기",
        DeviceOperation.Input => $"입력 {Value}",
        _ => DeviceLabels.Format(_owner.Operation, Value)
    };
    public bool IsSelected => _owner.Included && _owner.IsValid && _owner.Value == Value;
    public AsyncCommand SelectCommand { get; }
    public ScenarioSettingOption(ScenarioSetting owner, int value)
    {
        _owner = owner; Value = value;
        SelectCommand = new(() => { owner.Value = value; owner.Included = true; return Task.CompletedTask; });
    }
    public void NotifySelected() => Changed(nameof(IsSelected));
}
public sealed class ScenarioTarget : Bindable
{
    public RoleBinding Role { get; private set; }
    public DeviceConfig Device { get; private set; }
    public Capability[] Capabilities { get; private set; }
    public string Label => RoleChoice.From(Role, [Device]).Label + (Device.Location.Length > 0 ? $" · {Device.Location}" : "");
    public ScenarioTarget(RoleBinding role, DeviceConfig device, Capability[] capabilities)
    { Role = role; Device = device; Capabilities = capabilities; }
    public void Update(RoleBinding role, DeviceConfig device, Capability[] capabilities)
    { Role = role; Device = device; Capabilities = capabilities; Changed(nameof(Label)); }
}
