using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class DeviceConfigurationField : Bindable
{
    public DeviceSettingDefinition Definition { get; }
    public string Label => Definition.Label;
    public bool IsFixed => Definition.FixedValue is not null;
    public bool IsEditable => !IsFixed;
    public bool IsChoice => Definition.Kind is DeviceSettingKind.Choice or DeviceSettingKind.Boolean;
    public bool IsText => !IsChoice;
    public string[] Choices => Definition.Kind == DeviceSettingKind.Boolean ? ["true", "false"] : Definition.Choices;
    public string Hint => IsFixed ? "모델에서 자동 관리" : Definition.Minimum is not null || Definition.Maximum is not null
        ? $"허용 범위: {Definition.Minimum?.ToString() ?? "제한 없음"} ~ {Definition.Maximum?.ToString() ?? "제한 없음"}"
        : Definition.Required ? "필수" : "선택";
    private string _value;
    public string Value { get => _value; set { if (Set(ref _value, value ?? "")) Changed(nameof(Error)); } }
    public string Error => Definition.Accepts(Value) ? "" : $"{Label}: 값·허용 범위를 확인하세요.";
    public DeviceConfigurationField(DeviceSettingDefinition definition, string? value)
    { Definition = definition; _value = definition.FixedValue ?? value ?? definition.DefaultValue ?? ""; }
}
public sealed record RoleChoice(RoleBinding Binding, string Label)
{
    public string Id => Binding.Id;
    public static RoleChoice From(RoleBinding role, IEnumerable<DeviceConfig> devices)
    {
        var device = devices.FirstOrDefault(d => d.Id == role.DeviceId);
        var name = device?.Name ?? "배정 대상 없음";
        return new(role, string.IsNullOrWhiteSpace(role.Name) || role.IsDefault ? name : $"{name} · {role.Name}");
    }
}
