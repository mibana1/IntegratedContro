using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record ScenarioKindChoice(ScenarioStepKind Kind, string Label);
public sealed partial class MainViewModel
{
    public ScenarioKindChoice[] ScenarioKinds { get; } =
    [
        new(ScenarioStepKind.DeviceCommand, "장비 명령"),
        new(ScenarioStepKind.WaitUntil, "조건 충족까지 대기"),
        new(ScenarioStepKind.DisplayLayout, "저장 배치 표시")
    ];
    private ScenarioStepKind _draftStepKind;
    public ScenarioStepKind DraftStepKind
    {
        get => _draftStepKind;
        set
        {
            if (!Set(ref _draftStepKind, value)) return;
            Changed(nameof(IsDeviceScenarioStep)); Changed(nameof(IsDisplayScenarioStep));
            Changed(nameof(IsCommandScenarioStep)); Changed(nameof(ScenarioStepHint));
        }
    }
    public bool IsDeviceScenarioStep => DraftStepKind != ScenarioStepKind.DisplayLayout;
    public bool IsDisplayScenarioStep => DraftStepKind == ScenarioStepKind.DisplayLayout;
    public bool IsCommandScenarioStep => DraftStepKind == ScenarioStepKind.DeviceCommand;
    public string ScenarioStepHint => DraftStepKind switch
    {
        ScenarioStepKind.WaitUntil => "선택한 역할의 최신 가상 상태가 지정한 값과 같아질 때까지 조회합니다. 제한시간 초과 시 실패 정책을 적용합니다.",
        ScenarioStepKind.DisplayLayout => "시작 시 저장 배치를 고정합니다. Controller 응답 확인 후 다음 단계로 이동하며, 표시 종료는 배치의 기간 설정을 따릅니다.",
        _ => "선택한 역할에 명령을 한 번 전송합니다. 선택 조건은 전송 직전 한 번 확인합니다."
    };
    public ObservableCollection<SavedHiperwallLayout> ScenarioLayouts { get; } = [];
    private SavedHiperwallLayout? _selectedScenarioLayout;
    public SavedHiperwallLayout? SelectedScenarioLayout
    { get => _selectedScenarioLayout; set => Set(ref _selectedScenarioLayout, value); }

    private void RefreshScenarioLayouts(StateView state)
    {
        var id = SelectedScenarioLayout?.Id;
        Replace(ScenarioLayouts, state.SavedHiperwallLayouts);
        SelectedScenarioLayout = ScenarioLayouts.FirstOrDefault(x => x.Id == id);
    }
    private void RequireScenarioExtensions(IEnumerable<ScenarioStep> steps)
    {
        if (_state?.ScenarioExtensionsSupported != true && steps.Any(s => s.Kind != ScenarioStepKind.DeviceCommand))
            throw new ArgumentException("확장 단계를 사용하려면 최신 호스트에 연결하세요.");
    }
    private Task AddScenarioStep()
    {
        if (DraftStepKind != ScenarioStepKind.DeviceCommand && _state?.ScenarioExtensionsSupported != true)
            throw new ArgumentException("호스트를 최신 버전으로 시작한 뒤 다시 연결하세요.");
        if (DraftStepKind == ScenarioStepKind.DisplayLayout)
        {
            if (SelectedScenarioLayout is null) throw new ArgumentException("Hiperwall 편집에서 저장한 배치를 선택하세요.");
            DraftSteps.Add(new("", default, 0, DelayMs, TimeoutMs, DraftFailurePolicy,
                Kind: DraftStepKind, LayoutId: SelectedScenarioLayout.Id));
        }
        else
        {
            if (SelectedRole is null || SelectedCapability is null) throw new ArgumentException("역할과 기능을 선택하세요.");
            DeviceOperation? condition = !IsCommandScenarioStep || string.IsNullOrWhiteSpace(ConditionOperationText)
                ? null : Enum.Parse<DeviceOperation>(ConditionOperationText, true);
            int? expected = !IsCommandScenarioStep || string.IsNullOrWhiteSpace(ConditionValueText)
                ? null : int.Parse(ConditionValueText);
            DraftSteps.Add(new(SelectedRole.Id, SelectedCapability.Operation, CommandValue, DelayMs, TimeoutMs,
                DraftFailurePolicy, condition, expected, DraftStepKind));
        }
        return Task.CompletedTask;
    }
    private static string FormatScenarioStep(StepSnapshot step, StepRun run, int index)
    {
        var target = step.Display is { } d
            ? $"배치: {d.Layout.Name} v{d.Layout.Version} / 연결 v{d.Layout.ConfigurationVersion}\n   Controller: {d.Endpoint}\n   표시 요청 ID: {d.RequestId} / 대상 {d.Layout.Placements.Length}개 / 기간: {d.Layout.Duration}"
            : $"역할: {step.Role?.Id} → {step.TargetLabel}\n   고정 PC ID: {step.Target?.PcId} / 장비 ID: {step.Target?.Id}\n   장비 설정 v{step.Target?.Version} / 역할 v{step.Role?.Version}\n   {step.Operation} = {step.Value} {step.Unit} / 전송 전 조건: {step.ConditionOperation} = {step.ConditionValue}";
        return $"{index + 1}. {step.KindLabel}\n   {target}\n   시작 전 대기 {step.DelayBeforeMs}ms / 제한 {step.TimeoutMs}ms / 실패 정책 {step.OnFailure}\n" +
            $"   시작: {run.StartedAt?.ToLocalTime():HH:mm:ss} / 제한: {run.DeadlineAt?.ToLocalTime():HH:mm:ss}\n   {run.Status} / {run.Result}";
    }
}
