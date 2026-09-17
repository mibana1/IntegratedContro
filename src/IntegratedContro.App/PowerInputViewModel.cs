using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    public bool IsPowerCommand => SelectedCapability?.Operation == DeviceOperation.Power;
    public bool IsNumericCommand => !IsPowerCommand;
    private void NotifyCommandInput()
    {
        Changed(nameof(IsPowerCommand)); Changed(nameof(IsNumericCommand)); Changed(nameof(CommandRange));
        NotifyNumericInput();
    }
}
