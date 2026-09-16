using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    private int _selectedDraftStepIndex = -1;
    public int SelectedDraftStepIndex
    {
        get => _selectedDraftStepIndex;
        set { if (Set(ref _selectedDraftStepIndex, value)) NotifyScenarioEditor(); }
    }
    private bool HasSelectedDraftStep => SelectedDraftStepIndex >= 0 && SelectedDraftStepIndex < DraftSteps.Count;
    public string ScenarioOrderHint => HasSelectedDraftStep
        ? $"선택: {SelectedDraftStepIndex + 1} / {DraftSteps.Count}단계 · 변경 후 정의를 저장하세요."
        : $"총 {DraftSteps.Count}단계 · 이동하거나 삭제할 단계를 선택하세요.";
    private ScenarioDefinition? _selectedScenario;
    public ScenarioDefinition? SelectedScenario
    {
        get => _selectedScenario;
        set { if (Set(ref _selectedScenario, value)) NotifyScenarioEditor(); }
    }
    public string ScenarioDeletionHint => _state?.ScenarioDeletionSupported != true
        ? "시나리오 삭제에는 최신 ControlHost가 필요합니다."
        : !CanConfigure ? "삭제하려면 관리자 계정으로 사용을 시작하세요."
        : SelectedScenario is null ? "삭제할 저장 시나리오를 선택하세요."
        : ScenarioHasActiveJobs ? "진행 중인 작업이 있습니다. 작업·교대에서 완료 또는 취소 후 삭제하세요."
        : $"‘{SelectedScenario.Name}’ 정의를 삭제합니다. 실행 이력은 유지됩니다.";
    private bool ScenarioHasActiveJobs => SelectedScenario is { } definition &&
        _state?.Jobs.Any(j => j.Active && j.Snapshot.ScenarioId == definition.Id) == true;
    public AsyncCommand DeleteScenarioCommand { get; private set; } = null!;
    public AsyncCommand MoveStepUpCommand { get; private set; } = null!;
    public AsyncCommand MoveStepDownCommand { get; private set; } = null!;
    public AsyncCommand MoveStepFirstCommand { get; private set; } = null!;
    public AsyncCommand MoveStepLastCommand { get; private set; } = null!;

    private void InitializeScenarioEditor()
    {
        DraftSteps.CollectionChanged += (_, _) => NotifyScenarioEditor();
        MoveStepUpCommand = ScenarioEditCommand(() => MoveDraftStep(SelectedDraftStepIndex - 1),
            () => HasSelectedDraftStep && SelectedDraftStepIndex > 0);
        MoveStepDownCommand = ScenarioEditCommand(() => MoveDraftStep(SelectedDraftStepIndex + 1),
            () => HasSelectedDraftStep && SelectedDraftStepIndex < DraftSteps.Count - 1);
        MoveStepFirstCommand = ScenarioEditCommand(() => MoveDraftStep(0),
            () => HasSelectedDraftStep && SelectedDraftStepIndex > 0);
        MoveStepLastCommand = ScenarioEditCommand(() => MoveDraftStep(DraftSteps.Count - 1),
            () => HasSelectedDraftStep && SelectedDraftStepIndex < DraftSteps.Count - 1);
        DeleteScenarioCommand = Command(async () =>
        {
            var definition = SelectedScenario!;
            await Client.Post<bool>("/api/scenarios/delete", new DeleteScenarioRequest(Generation, definition.Id, definition.Version));
            Scenarios.Remove(definition);
            SelectedScenario = null;
            if (_scenarioId == definition.Id) ClearScenarioDraft();
            Message = $"‘{definition.Name}’ 시나리오 정의를 삭제했습니다. 기존 실행 이력은 유지됩니다.";
        }, () => CanConfigure && _state?.ScenarioDeletionSupported == true && SelectedScenario is not null && !ScenarioHasActiveJobs);
    }
    private AsyncCommand ScenarioEditCommand(Action action, Func<bool> available)
    {
        var command = new AsyncCommand(() => { action(); return Task.CompletedTask; },
            () => !_busy && !_closing && available());
        _commands.Add(command);
        return command;
    }
    private void MoveDraftStep(int destination)
    {
        var source = SelectedDraftStepIndex;
        // Use the row index: two valid steps may have exactly the same values.
        SelectedDraftStepIndex = -1;
        DraftSteps.Move(source, destination);
        SelectedDraftStepIndex = destination;
    }
    private void RemoveSelectedDraftStep()
    {
        var index = SelectedDraftStepIndex;
        SelectedDraftStepIndex = -1;
        DraftSteps.RemoveAt(index);
        SelectedDraftStepIndex = Math.Min(index, DraftSteps.Count - 1);
    }
    private void ClearScenarioDraft()
    {
        _scenarioId = Guid.NewGuid(); _scenarioVersion = 0; ScenarioName = "";
        SelectedDraftStepIndex = -1; DraftSteps.Clear(); Changed(nameof(ScenarioName));
    }
    private void NotifyScenarioEditor()
    {
        Changed(nameof(ScenarioOrderHint)); Changed(nameof(ScenarioDeletionHint));
        foreach (var command in _commands) command.Raise();
    }
}
