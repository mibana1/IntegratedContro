using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record ConditionOperationChoice(string Value, string Label);

public sealed partial class MainViewModel
{
    public bool IsPowerCommand => SelectedCapability?.Operation == DeviceOperation.Power;
    public bool IsNumericCommand => !IsPowerCommand;
    private void NotifyCommandInput()
    {
        Changed(nameof(IsPowerCommand)); Changed(nameof(IsNumericCommand)); Changed(nameof(CommandRange));
        NotifyNumericInput();
    }
    public ConditionOperationChoice[] ConditionOperations =>
        [new("", "조건 없음"), .. (SelectedScenarioTarget?.Capabilities ?? [])
            .Where(c => c.CanRead && c.Operation != DeviceOperation.Stop)
            .Select(c => new ConditionOperationChoice(c.Operation.ToString(), DeviceLabels.Operation(c.Operation)))];
    private string _conditionOperationText = "";
    public string ConditionOperationText
    {
        get => _conditionOperationText;
        set
        {
            if (!Set(ref _conditionOperationText, value ?? "")) return;
            ConditionValueText = SelectedScenarioTarget?.Capabilities.FirstOrDefault(c => c.Operation.ToString() == value)?.Minimum.ToString() ?? "";
            Changed(nameof(IsPowerCondition)); Changed(nameof(IsNumericCondition)); Changed(nameof(ConditionRange));
        }
    }
    private string _conditionValueText = "";
    public string ConditionValueText
    {
        get => _conditionValueText;
        set { if (Set(ref _conditionValueText, value ?? "")) Changed(nameof(ConditionPowerValue)); }
    }
    public int? ConditionPowerValue
    {
        get => int.TryParse(ConditionValueText, out var value) && value is 0 or 1 ? value : null;
        set => ConditionValueText = value?.ToString() ?? "";
    }
    public bool IsPowerCondition => ConditionOperationText == nameof(DeviceOperation.Power);
    public bool IsNumericCondition => ConditionOperationText.Length > 0 && !IsPowerCondition;
    public string ConditionRange
    {
        get
        {
            var capability = SelectedScenarioTarget?.Capabilities.FirstOrDefault(c => c.Operation.ToString() == ConditionOperationText);
            return capability is null || IsPowerCondition ? "" : $"{capability.Minimum}~{capability.Maximum} {capability.Unit}";
        }
    }
    private void ResetScenarioCondition()
    {
        ConditionOperationText = ""; ConditionValueText = "";
        Changed(nameof(ConditionOperations)); Changed(nameof(ConditionOperationText)); Changed(nameof(ConditionRange));
    }
}
