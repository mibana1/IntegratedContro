using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class DeviceControlViewModel
{
    public bool IsPowerCommand => SelectedCapability?.Operation == DeviceOperation.Power;
    public bool IsNumericCommand => !IsPowerCommand;
    public bool IsRangeCommand => SelectedCapability?.Operation is DeviceOperation.Brightness or DeviceOperation.Volume;
    public int CommandMinimum => SelectedCapability?.Minimum ?? 0;
    public int CommandMaximum => SelectedCapability?.Maximum ?? 100;
    private void NotifyCommandInput()
    {
        Changed(nameof(IsPowerCommand)); Changed(nameof(IsNumericCommand)); Changed(nameof(IsRangeCommand));
        Changed(nameof(CommandMinimum)); Changed(nameof(CommandMaximum)); Changed(nameof(CommandRange));
        NotifyNumericInput();
    }
}
