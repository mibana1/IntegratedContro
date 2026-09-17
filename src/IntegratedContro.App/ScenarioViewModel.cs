using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record ScenarioKindChoice(ScenarioStepKind Kind, string Label);
public sealed partial class ScenarioEditorViewModel
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
            Changed(nameof(IsCommandScenarioStep)); Changed(nameof(ScenarioStepHint)); RefreshScenarioSettings(reset: false);
            NotifyNumericInput();
        }
    }
    public bool IsDeviceScenarioStep => DraftStepKind != ScenarioStepKind.DisplayLayout;
    public bool IsDisplayScenarioStep => DraftStepKind == ScenarioStepKind.DisplayLayout;
    public bool IsCommandScenarioStep => DraftStepKind == ScenarioStepKind.DeviceCommand;
    public string ScenarioStepHint => DraftStepKind switch
    {
        ScenarioStepKind.WaitUntil => "선택한 장비의 조건을 위에서부터 순서대로 대기합니다. 제한시간 초과 시 실패 정책을 적용합니다.",
        ScenarioStepKind.DisplayLayout => "시작 시 저장 배치를 고정합니다. Controller 응답 확인 후 다음 단계로 이동하며, 표시 종료는 배치의 기간 설정을 따릅니다.",
        _ => "선택한 장비에서 체크한 설정을 순서대로 추가합니다. 편집 중에는 장비를 조작하지 않습니다."
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
        if (State?.ScenarioExtensionsSupported != true && steps.Any(s => s.Kind != ScenarioStepKind.DeviceCommand))
            throw new ArgumentException("확장 단계를 사용하려면 최신 호스트에 연결하세요.");
    }
    private Task AddScenarioStep()
    {
        if (ScenarioTimingError.Length > 0) throw new ArgumentException(ScenarioTimingError);
        if (DraftStepKind != ScenarioStepKind.DeviceCommand && State?.ScenarioExtensionsSupported != true)
            throw new ArgumentException("호스트를 최신 버전으로 시작한 뒤 다시 연결하세요.");
        ScenarioStep[] steps;
        if (DraftStepKind == ScenarioStepKind.DisplayLayout)
        {
            if (SelectedScenarioLayout is null) throw new ArgumentException("Hiperwall 편집에서 저장한 배치를 선택하세요.");
            steps = [new("", default, 0, DelayMs, TimeoutMs, DraftFailurePolicy, Kind: DraftStepKind, LayoutId: SelectedScenarioLayout.Id)];
        }
        else steps = ReadScenarioDeviceSteps();
        if (DraftSteps.Count + steps.Length > 100) throw new ArgumentException("시나리오는 최대 100단계입니다.");
        foreach (var step in steps) DraftSteps.Add(step);
        ReportStatus($"{steps.Length}개 설정을 마지막 단계로 추가했습니다. 정의를 저장한 뒤 실행하세요.");
        return Task.CompletedTask;
    }
}
