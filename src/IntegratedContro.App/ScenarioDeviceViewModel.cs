using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    public ObservableCollection<ScenarioTarget> ScenarioTargets { get; } = [];
    public ObservableCollection<ScenarioSetting> ScenarioSettings { get; } = [];
    private ScenarioTarget? _scenarioTarget;
    public ScenarioTarget? SelectedScenarioTarget
    {
        get => _scenarioTarget;
        set { if (Set(ref _scenarioTarget, value)) RefreshScenarioSettings(reset: true); }
    }
    public string ScenarioSettingsHint => SelectedScenarioTarget is null ? "역할이 배정된 장비를 선택하세요. 역할은 관리자 설정에서 배정합니다." :
        "추가할 설정을 체크하세요. ON/OFF 버튼이나 값을 바꾸면 자동 선택됩니다. 위에서부터 각각 한 단계로 추가합니다.";
    private void RefreshScenarioTargets(StateView state)
    {
        var previousRole = _scenarioTarget?.Role; var previousDevice = _scenarioTarget?.Device;
        var previousCapabilities = _scenarioTarget?.Capabilities;
        foreach (var removed in ScenarioTargets.Where(t => !state.Roles.Any(r => r.Id == t.Role.Id &&
            state.Devices.Any(d => d.Id == r.DeviceId && d.Enabled))).ToArray()) ScenarioTargets.Remove(removed);
        foreach (var role in state.Roles)
        {
            var device = state.Devices.SingleOrDefault(d => d.Id == role.DeviceId && d.Enabled);
            var model = state.Models.SingleOrDefault(m => m.Id == device?.ModelId);
            if (device is null || model is null) continue;
            var existing = ScenarioTargets.SingleOrDefault(t => t.Role.Id == role.Id);
            if (existing is null) ScenarioTargets.Add(new(role, device, model.Capabilities));
            else existing.Update(role, device, model.Capabilities);
        }
        if (_scenarioTarget is not null && !ScenarioTargets.Contains(_scenarioTarget)) SelectedScenarioTarget = null;
        else if (_scenarioTarget is { } target && (previousRole != target.Role || previousDevice is null ||
            !target.Device.MatchesExecutionTarget(previousDevice) || previousCapabilities?.SequenceEqual(target.Capabilities) != true))
            RefreshScenarioSettings(reset: true);
    }
    private void RefreshScenarioSettings(bool reset)
    {
        if (reset) ResetScenarioCondition();
        var capabilities = _scenarioTarget?.Capabilities.Where(c => IsCommandScenarioStep || (c.CanRead && c.Operation != DeviceOperation.Stop)).ToArray() ?? [];
        var old = reset ? [] : ScenarioSettings.ToArray();
        // Preserve input objects during ordinary polling and compatible kind switches.
        if (!reset && ScenarioSettings.Select(s => s.Capability).SequenceEqual(capabilities)) return;
        ScenarioSettings.Clear();
        foreach (var capability in capabilities)
            ScenarioSettings.Add(old.SingleOrDefault(s => s.Capability == capability) ?? new ScenarioSetting(capability));
        Changed(nameof(ScenarioSettingsHint));
    }
    private ScenarioStep[] ReadScenarioDeviceSteps()
    {
        if (SelectedScenarioTarget is null || !ScenarioTargets.Contains(SelectedScenarioTarget))
            throw new ArgumentException("역할이 배정된 장비를 선택하세요.");
        var settings = ScenarioSettings.Where(s => s.Included).ToArray();
        if (settings.Length == 0) throw new ArgumentException("추가할 설정을 체크하거나 ON/OFF 버튼·조절값을 선택하세요.");
        if (settings.Any(s => !s.IsValid)) throw new ArgumentException("선택한 설정의 허용 범위와 숫자 입력을 확인하세요.");
        DeviceOperation? condition = !IsCommandScenarioStep || string.IsNullOrWhiteSpace(ConditionOperationText)
            ? null : Enum.Parse<DeviceOperation>(ConditionOperationText, true);
        int? expected = !IsCommandScenarioStep || string.IsNullOrWhiteSpace(ConditionValueText) ? null : int.Parse(ConditionValueText);
        return settings.Select((s, i) => new ScenarioStep(SelectedScenarioTarget.Role.Id, s.Operation, s.Value,
            i == 0 ? DelayMs : 0, TimeoutMs, DraftFailurePolicy, condition, expected, DraftStepKind)).ToArray();
    }
}
