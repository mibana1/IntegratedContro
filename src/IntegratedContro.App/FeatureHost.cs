using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    // Only this adapter knows the shell and HTTP routes. Feature tests replace its typed contracts.
    private sealed class FeatureHost(MainViewModel owner) : ILightingHost, IScenarioHost
    {
        public Task RunAsync(Func<Task> action) => owner.RunCommand(action);
        public void ReportStatus(string message) => owner.Message = message;
        public Task<LightLayout> SaveLayoutAsync(LightOrderRequest request) => owner.Client.Post<LightLayout>("/api/layout/lights", request);
        public Task<DeviceState> ReconcileAsync(ReconcileRequest request) => owner.Client.Post<DeviceState>("/api/devices/reconcile", request);
        public Task<ScenarioDefinition> SaveAsync(ScenarioRequest request) => owner.Client.Post<ScenarioDefinition>("/api/scenarios", request);
        public async Task DeleteAsync(DeleteScenarioRequest request) => await owner.Client.Post<bool>("/api/scenarios/delete", request);
        public Task SubmitAsync(SubmitRequest request)
        {
            if (owner.HasPending) throw new InvalidOperationException("이전 요청의 접수 여부를 먼저 확인하세요.");
            owner._pending = request;
            owner.Notify();
            return owner.SendPending();
        }
        public Task SubmitBatchAsync(LightBatchRequest request)
        {
            if (owner.HasPending) throw new InvalidOperationException("이전 요청의 접수 여부를 먼저 확인하세요.");
            owner._pendingLightBatch = request;
            owner.Notify();
            return owner.SendPending();
        }
    }
}
