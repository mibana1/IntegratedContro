using System.Globalization;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    // Keep the actual editor text, including invalid input. Never retain an older numeric value for dispatch.
    private string _commandValueText = "1", _delayMsText = "0", _timeoutMsText = "3000";
    public string CommandValueText
    {
        get => _commandValueText;
        set { if (Set(ref _commandValueText, value ?? "")) NotifyNumericInput(); }
    }
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
    public int CommandValue { get => ParseInteger(CommandValueText); set => CommandValueText = FormatInteger(value); }
    public int DelayMs { get => ParseInteger(DelayMsText); set => DelayMsText = FormatInteger(value); }
    public int TimeoutMs { get => ParseInteger(TimeoutMsText); set => TimeoutMsText = FormatInteger(value); }
    public int? CommandPowerValue
    {
        get => TryInteger(CommandValueText, out var value) && value is 0 or 1 ? value : null;
        set => CommandValueText = value is { } number ? FormatInteger(number) : "";
    }
    public string ManualInputError
    {
        get
        {
            if (SelectedCapability is not { } capability) return "";
            if (!InRange(CommandValueText, capability.Minimum, capability.Maximum))
                return $"값은 {capability.Minimum}~{capability.Maximum} {capability.Unit} 범위의 정수로 입력하세요.";
            return TimingInputError(30000);
        }
    }
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
        Changed(nameof(CommandPowerValue)); Changed(nameof(ManualInputError));
        SubmitCommand?.Raise();
    }
}
