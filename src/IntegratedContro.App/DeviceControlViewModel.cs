using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class DeviceControlViewModel : FeatureViewModel
{
    private readonly IFeatureSession _host;
    public ObservableCollection<RoleBinding> Roles { get; } = [];
    public ObservableCollection<RoleChoice> RoleChoices { get; } = [];
    public RoleChoice? SelectedRoleChoice
    {
        get => RoleChoices.FirstOrDefault(c => c.Id == SelectedRole?.Id);
        set { if (value is not null) SelectedRole = Roles.FirstOrDefault(r => r.Id == value.Id); }
    }
    public AsyncCommand SubmitCommand { get; }
    public DeviceControlViewModel(IFeatureSession host) : base(host)
    {
        _host = host;
        SubmitCommand = Command(Submit, () => CanControl && SelectedRole is not null && SelectedCapability is not null && !HasPending && ManualInputError.Length == 0);
    }
    protected override void OnContextChanged(FeatureContext previous)
    {
        if (previous.State?.Session.Id != State?.Session.Id)
        {
            _selectedRole = null; _selectedCapability = null;
            CommandValueText = "1"; DelayMsText = "0"; TimeoutMsText = "3000";
        }
        if (!ReferenceEquals(previous.State, State))
        {
            var roleId = _selectedRole?.Id; var operation = _selectedCapability?.Operation;
            Replace(Roles, State?.Roles ?? []);
            Replace(RoleChoices, Roles.Select(r => RoleChoice.From(r, State?.Devices ?? [])));
            _selectedRole = Roles.FirstOrDefault(r => r.Id == roleId);
            _selectedCapability = Capabilities.FirstOrDefault(c => c.Operation == operation);
            Changed(nameof(SelectedRole)); Changed(nameof(SelectedRoleChoice)); Changed(nameof(SelectedCapability)); Changed(nameof(Capabilities)); NotifyCommandInput();
        }
    }
    private RoleBinding? _selectedRole;
    public RoleBinding? SelectedRole
    {
        get => _selectedRole;
        set { Set(ref _selectedRole, value); Changed(nameof(SelectedRoleChoice)); Changed(nameof(Capabilities)); SelectedCapability = Capabilities.FirstOrDefault(); }
    }
    public IEnumerable<Capability> Capabilities => State?.Models.FirstOrDefault(m =>
        m.Id == State.Devices.FirstOrDefault(d => d.Id == SelectedRole?.DeviceId)?.ModelId)?.Capabilities ?? [];
    private Capability? _selectedCapability;
    public Capability? SelectedCapability
    {
        get => _selectedCapability;
        set { Set(ref _selectedCapability, value); if (value is not null) { CommandValue = value.Minimum; Changed(nameof(CommandValue)); } NotifyCommandInput(); }
    }
    public string CommandRange => SelectedCapability is null ? "역할을 선택하세요." : IsPowerCommand ? "전원 상태를 선택한 뒤 명령을 접수하세요." : $"{SelectedCapability.Minimum}~{SelectedCapability.Maximum} {SelectedCapability.Unit}";

    private async Task Submit()
    {
        if (SelectedCapability is null) throw new ArgumentException("지원 기능을 선택하세요.");
        if (ManualInputError.Length > 0) throw new ArgumentException(ManualInputError);
        var request = new SubmitRequest(Guid.NewGuid(), Generation, SelectedRole!.Id, SelectedCapability.Operation, CommandValue,
            DelayBeforeMs: DelayMs, TimeoutMs: TimeoutMs);
        await _host.SubmitAsync(request);
    }
}
