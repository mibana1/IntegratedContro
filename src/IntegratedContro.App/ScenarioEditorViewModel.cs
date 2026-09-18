using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class ScenarioEditorViewModel : FeatureViewModel
{
    private readonly IScenarioHost _host;
    public ObservableCollection<ScenarioDefinition> Scenarios { get; } = [];
    public ObservableCollection<ScenarioStep> DraftSteps { get; } = [];
    public IReadOnlyDictionary<string, string> TargetNames => (State?.Roles ?? []).ToDictionary(r => r.Id,
        r => RoleChoice.From(r, State?.Devices ?? []).Label)
        .Concat((State?.SavedHiperwallLayouts ?? []).Select(l => new KeyValuePair<string, string>($"layout:{l.Id}", l.Name)))
        .ToDictionary(p => p.Key, p => p.Value);
    public IEnumerable<FailurePolicy> FailurePolicies => Enum.GetValues<FailurePolicy>();
    public FailurePolicy DraftFailurePolicy { get; set; }
    private string _scenarioName = "";
    public string ScenarioName { get => _scenarioName; set => Set(ref _scenarioName, value); }
    private Guid _scenarioId = Guid.NewGuid();
    private int _scenarioVersion;
    public AsyncCommand AddStepCommand { get; }
    public AsyncCommand RemoveStepCommand { get; }
    public AsyncCommand SaveScenarioCommand { get; }
    public AsyncCommand LoadScenarioCommand { get; }
    public AsyncCommand NewScenarioCommand { get; }
    public AsyncCommand RunScenarioCommand { get; }

    public ScenarioEditorViewModel(IScenarioHost host) : base(host)
    {
        _host = host;
        AddStepCommand = Command(AddScenarioStep, () => ScenarioTimingError.Length == 0);
        InitializeScenarioEditor();
        RemoveStepCommand = LocalCommand(RemoveSelectedDraftStep, () => HasSelectedDraftStep);
        SaveScenarioCommand = Command(async () =>
        {
            RequireScenarioExtensions(DraftSteps);
            var saved = await _host.SaveAsync(new ScenarioRequest(Generation, _scenarioId, ScenarioName, DraftSteps.ToArray(), _scenarioVersion));
            _scenarioVersion = saved.Version; ReportStatus("시나리오 정의를 저장했습니다.");
        }, () => CanConfigure);
        LoadScenarioCommand = Command(() =>
        {
            if (SelectedScenario is null) return Task.CompletedTask;
            _scenarioId = SelectedScenario.Id; _scenarioVersion = SelectedScenario.Version; ScenarioName = SelectedScenario.Name;
            SelectedDraftStepIndex = -1;
            DraftSteps.Clear(); foreach (var step in SelectedScenario.Steps) DraftSteps.Add(step);
            Changed(nameof(ScenarioName)); return Task.CompletedTask;
        });
        NewScenarioCommand = Command(() => { ClearScenarioDraft(); return Task.CompletedTask; });
        RunScenarioCommand = Command(() => RunScenario(), () => CanControl && !HasPending && SelectedScenario is not null);
    }

    protected override void OnContextChanged(FeatureContext previous)
    {
        if (!ReferenceEquals(previous.State, State))
        {
            if (State is { } state)
            {
                var id = SelectedScenario?.Id;
                RefreshScenarioTargets(state);
                RefreshScenarioLayouts(state);
                Replace(Scenarios, state.Scenarios);
                SelectedScenario = Scenarios.FirstOrDefault(s => s.Id == id);
            }
            else
            {
                Scenarios.Clear(); SelectedScenario = null;
                ScenarioLayouts.Clear(); SelectedScenarioLayout = null;
                ScenarioTargets.Clear(); SelectedScenarioTarget = null;
            }
        }
        Changed(nameof(TargetNames)); NotifyScenarioEditor();
    }

    private Task RunScenario()
    {
        if (SelectedScenario is null) throw new ArgumentException("실행할 시나리오를 선택하세요.");
        RequireScenarioExtensions(SelectedScenario.Steps);
        return _host.SubmitAsync(new(Guid.NewGuid(), Generation, null, ScenarioId: SelectedScenario.Id));
    }

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
    public string ScenarioDeletionHint => State?.ScenarioDeletionSupported != true
        ? "시나리오 삭제에는 최신 ControlHost가 필요합니다."
        : !CanConfigure ? "삭제하려면 관리자 계정으로 사용을 시작하세요."
        : SelectedScenario is null ? "삭제할 저장 시나리오를 선택하세요."
        : ScenarioHasActiveJobs ? "진행 중인 작업이 있습니다. 작업·교대에서 완료 또는 취소 후 삭제하세요."
        : $"‘{SelectedScenario.Name}’ 정의를 삭제합니다. 실행 이력은 유지됩니다.";
    private bool ScenarioHasActiveJobs => SelectedScenario is { } definition &&
        State?.Jobs.Any(j => j.Active && j.Snapshot.ScenarioId == definition.Id) == true;
    public AsyncCommand DeleteScenarioCommand { get; private set; } = null!;
    public AsyncCommand MoveStepUpCommand { get; private set; } = null!;
    public AsyncCommand MoveStepDownCommand { get; private set; } = null!;
    public AsyncCommand MoveStepFirstCommand { get; private set; } = null!;
    public AsyncCommand MoveStepLastCommand { get; private set; } = null!;

    private void InitializeScenarioEditor()
    {
        DraftSteps.CollectionChanged += (_, _) => NotifyScenarioEditor();
        MoveStepUpCommand = LocalCommand(() => MoveDraftStep(SelectedDraftStepIndex - 1),
            () => HasSelectedDraftStep && SelectedDraftStepIndex > 0);
        MoveStepDownCommand = LocalCommand(() => MoveDraftStep(SelectedDraftStepIndex + 1),
            () => HasSelectedDraftStep && SelectedDraftStepIndex < DraftSteps.Count - 1);
        MoveStepFirstCommand = LocalCommand(() => MoveDraftStep(0),
            () => HasSelectedDraftStep && SelectedDraftStepIndex > 0);
        MoveStepLastCommand = LocalCommand(() => MoveDraftStep(DraftSteps.Count - 1),
            () => HasSelectedDraftStep && SelectedDraftStepIndex < DraftSteps.Count - 1);
        DeleteScenarioCommand = Command(async () =>
        {
            var definition = SelectedScenario!;
            await _host.DeleteAsync(new DeleteScenarioRequest(Generation, definition.Id, definition.Version));
            Scenarios.Remove(definition);
            SelectedScenario = null;
            if (_scenarioId == definition.Id) ClearScenarioDraft();
            ReportStatus($"‘{definition.Name}’ 시나리오 정의를 삭제했습니다. 기존 실행 이력은 유지됩니다.");
        }, () => CanConfigure && State?.ScenarioDeletionSupported == true && SelectedScenario is not null && !ScenarioHasActiveJobs);
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
        RefreshCommands();
    }
}
