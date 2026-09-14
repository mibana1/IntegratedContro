using System.Collections.ObjectModel;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    public ObservableCollection<SavedHiperwallLayout> SavedLayouts { get; } = [];
    private SavedHiperwallLayout? _selectedSavedLayout;
    public SavedHiperwallLayout? SelectedSavedLayout
    {
        get => _selectedSavedLayout;
        set { if (Set(ref _selectedSavedLayout, value)) { Changed(nameof(SavedLayoutDetails)); Notify(); } }
    }
    public string SavedLayoutDetails => SelectedSavedLayout is not { } layout ? "저장 배치를 선택하세요." :
        $"{layout.Name} v{layout.Version} · {layout.Entries.Length}개\n{layout.Endpoint} · 연결 설정 v{layout.ConfigurationVersion}\n" +
        string.Join("\n", layout.Entries.Select(e => $"{e.Name} · {e.Selector}={e.ContentValue} · Zone {e.ZoneId}\n중심 ({e.Bounds.X}, {e.Bounds.Y}), {e.Bounds.Width}×{e.Bounds.Height} · 음량 {e.Volume} / 음소거 {e.Muted}"));
    private string _savedLayoutName = "";
    public string SavedLayoutName { get => _savedLayoutName; set => Set(ref _savedLayoutName, value); }
    private Guid _layoutId = Guid.NewGuid();
    private int _layoutVersion;
    private Guid? _layoutSession;
    public string LayoutEditSummary => _layoutVersion == 0 ? "새 배치로 저장" : $"배치 v{_layoutVersion}을 현재 LIVE로 갱신";
    public int ExtensionTimeoutMs { get; set; } = 60000;
    public int ConditionPollMs { get; set; } = 1000;
    public AsyncCommand CaptureLayoutCommand { get; private set; } = null!;
    public AsyncCommand LoadLayoutCommand { get; private set; } = null!;
    public AsyncCommand NewLayoutCommand { get; private set; } = null!;
    public AsyncCommand DeleteLayoutCommand { get; private set; } = null!;
    public AsyncCommand ShowLayoutCommand { get; private set; } = null!;
    public AsyncCommand AddWaitStepCommand { get; private set; } = null!;
    public AsyncCommand AddLayoutStepCommand { get; private set; } = null!;
    public AsyncCommand MoveStepUpCommand { get; private set; } = null!;
    public AsyncCommand MoveStepDownCommand { get; private set; } = null!;
    private void InitializeScenarioExtensions()
    {
        NewLayoutCommand = Command(() =>
        {
            _layoutId = Guid.NewGuid(); _layoutVersion = 0; SavedLayoutName = "";
            Changed(nameof(SavedLayoutName)); Changed(nameof(LayoutEditSummary)); return Task.CompletedTask;
        });
        LoadLayoutCommand = Command(() =>
        {
            var layout = SelectedSavedLayout!; _layoutId = layout.Id; _layoutVersion = layout.Version; SavedLayoutName = layout.Name;
            Changed(nameof(SavedLayoutName)); Changed(nameof(LayoutEditSummary)); return Task.CompletedTask;
        }, () => SelectedSavedLayout is not null);
        CaptureLayoutCommand = Command(async () =>
        {
            var saved = await Client.Post<SavedHiperwallLayout>("/api/hiperwall/layouts/capture",
                new CaptureHiperwallLayoutRequest(Generation, _layoutId, SavedLayoutName, _layoutVersion,
                    _state!.HiperwallConfigurationVersion, HiperwallEditing.Revision(Hiperwall.Instances.Select(i => i.Item)),
                    Hiperwall.TargetZone?.Item.Id));
            _layoutVersion = saved.Version; Changed(nameof(LayoutEditSummary));
            await Refresh(); SelectedSavedLayout = SavedLayouts.Single(l => l.Id == saved.Id);
            Message = "현재 LIVE 배치를 저장했습니다. 저장만으로 영상벽 표시를 변경하지 않습니다.";
        }, () => CanConfigure && _state?.ScenarioExtensionsSupported == true);
        DeleteLayoutCommand = Command(async () =>
        {
            var layout = SelectedSavedLayout!;
            await Client.Post<bool>("/api/hiperwall/layouts/delete", new DeleteHiperwallLayoutRequest(Generation, layout.Id, layout.Version));
            Message = "저장 배치를 삭제했습니다. 화면의 열린 인스턴스는 유지됩니다.";
        }, () => CanConfigure && SelectedSavedLayout is not null);
        ShowLayoutCommand = Command(async () =>
        {
            _pending = new(Guid.NewGuid(), Generation, null, TimeoutMs: ExtensionTimeoutMs,
                SavedLayoutId: SelectedSavedLayout!.Id, SavedLayoutVersion: SelectedSavedLayout.Version);
            Notify(); await SendPending();
        }, () => CanControl && !HasPending && _state?.CanControlHiperwall == true && !_state.HiperwallReserved && SelectedSavedLayout is not null);
        AddWaitStepCommand = Command(() =>
        {
            if (SelectedRole is null || SelectedCapability is not { CanRead: true }) throw new ArgumentException("장비 제어에서 읽기 가능한 역할·기능·조건 값을 선택하세요.");
            DraftSteps.Add(new(SelectedRole.Id, SelectedCapability.Operation, CommandValue, DelayMs, ExtensionTimeoutMs, DraftFailurePolicy)
                { Kind = ScenarioStepKind.WaitUntil, PollIntervalMs = ConditionPollMs });
            return Task.CompletedTask;
        }, () => _state?.ScenarioExtensionsSupported == true);
        AddLayoutStepCommand = Command(() =>
        {
            DraftSteps.Add(new("", DeviceOperation.Power, 0, DelayMs, ExtensionTimeoutMs, DraftFailurePolicy)
                { Kind = ScenarioStepKind.ShowLayout, SavedLayoutId = SelectedSavedLayout!.Id, SavedLayoutName = SelectedSavedLayout.Name });
            return Task.CompletedTask;
        }, () => _state?.ScenarioExtensionsSupported == true && SelectedSavedLayout is not null);
        MoveStepUpCommand = Command(() => MoveDraftStep(-1));
        MoveStepDownCommand = Command(() => MoveDraftStep(1));
    }
    private Task MoveDraftStep(int direction)
    {
        if (SelectedDraftStep is not null)
        {
            var index = DraftSteps.IndexOf(SelectedDraftStep); var target = index + direction;
            if (index >= 0 && target >= 0 && target < DraftSteps.Count) DraftSteps.Move(index, target);
        }
        return Task.CompletedTask;
    }
    private void RefreshSavedLayouts()
    {
        var id = SelectedSavedLayout?.Id;
        if (_layoutSession != _login?.Session.Id)
        {
            _layoutSession = _login?.Session.Id; id = null; _layoutId = Guid.NewGuid(); _layoutVersion = 0; SavedLayoutName = "";
            Changed(nameof(SavedLayoutName)); Changed(nameof(LayoutEditSummary));
        }
        Replace(SavedLayouts, IsLoggedIn ? _state?.HiperwallLayouts ?? [] : []);
        _selectedSavedLayout = SavedLayouts.FirstOrDefault(l => l.Id == id);
        Changed(nameof(SelectedSavedLayout)); Changed(nameof(SavedLayoutDetails));
    }
}
