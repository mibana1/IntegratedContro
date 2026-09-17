using System.Globalization;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record ConditionOperationChoice(string Value, string Label);
public sealed partial class ScenarioEditorViewModel
{
    private string _delayMsText = "0", _timeoutMsText = "3000";
    public string DelayMsText
    {
        get => _delayMsText;
        set { if (Set(ref _delayMsText, value ?? "")) NotifyNumericInput(); }
    }
    public string TimeoutMsText
    {
        get => _timeoutMsText;
        set { if (Set(ref _timeoutMsText, value ?? "")) NotifyNumericInput(); }
    }
    public int DelayMs { get => ParseInteger(DelayMsText); set => DelayMsText = FormatInteger(value); }
    public int TimeoutMs { get => ParseInteger(TimeoutMsText); set => TimeoutMsText = FormatInteger(value); }
    public string ScenarioTimingError => TimingInputError(DraftStepKind == ScenarioStepKind.DeviceCommand ? 30000 : 3600000);
    private string TimingInputError(int maximumTimeout)
    {
        if (!InRange(DelayMsText, 0, 3600000)) return "시작 전 대기는 0~3600000ms 범위의 정수로 입력하세요.";
        if (!InRange(TimeoutMsText, 100, maximumTimeout)) return $"제한시간은 100~{maximumTimeout}ms 범위의 정수로 입력하세요.";
        return "";
    }
    private static bool TryInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static int ParseInteger(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static string FormatInteger(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool InRange(string text, int minimum, int maximum) =>
        TryInteger(text, out var value) && value >= minimum && value <= maximum;
    private void NotifyNumericInput()
    {
        Changed(nameof(ScenarioTimingError)); AddStepCommand?.Raise();
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
