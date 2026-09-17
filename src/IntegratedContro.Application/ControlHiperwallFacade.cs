using IntegratedContro.Core;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public HiperwallSettingsView GetHiperwallSettings(string token) => _wall.GetHiperwallSettings(token);
    public HiperwallView GetHiperwallStatus(string token) => _wall.GetHiperwallStatus(token);
    public HiperwallSettingsView SaveHiperwallSettings(string token, SaveHiperwallRequest request) => _wall.SaveHiperwallSettings(token, request);
    public Task<HiperwallView> RefreshHiperwallAsync(string token, bool connectionTest, CancellationToken ct) => _wall.RefreshHiperwallAsync(token, connectionTest, ct);
    public HiperwallEditReceipt[] GetHiperwallEdits(string token) => _wall.GetHiperwallEdits(token);
    public HiperwallEditReceipt GetHiperwallEdit(string token, Guid id) => _wall.GetHiperwallEdit(token, id);
    public Task<HiperwallEditReceipt> EditHiperwallAsync(string token, HiperwallEditRequest request, CancellationToken ct) => _wall.EditHiperwallAsync(token, request, ct);
    public HiperwallEditReceipt CancelHiperwallEdit(string token, JobActionRequest request) => _wall.CancelHiperwallEdit(token, request);
    public Task DispatchHiperwallNextAsync(CancellationToken stopping) => _wall.DispatchHiperwallNextAsync(stopping);
    public HiperwallDisplayView GetHiperwallDisplays(string token) => _wall.GetHiperwallDisplays(token);
    public SavedHiperwallLayout SaveHiperwallLayout(string token, SaveHiperwallLayoutRequest request) => _wall.SaveHiperwallLayout(token, request);
    public bool DeleteHiperwallLayout(string token, DeleteHiperwallLayoutRequest request) => _wall.DeleteHiperwallLayout(token, request);
    public Task<HiperwallDisplayJob> DisplayHiperwallAsync(string token, HiperwallDisplayRequest request, CancellationToken ct) => _wall.DisplayHiperwallAsync(token, request, ct);
    public HiperwallDisplayJob StopHiperwallDisplay(string token, JobActionRequest request) => _wall.StopHiperwallDisplay(token, request);
    public Task CleanupHiperwallOnShutdownAsync(CancellationToken ct) => _wall.CleanupHiperwallOnShutdownAsync(ct);
    public Task ReconcileHiperwallDisplaysAsync(CancellationToken ct, bool shutdown = false) => _wall.ReconcileHiperwallDisplaysAsync(ct, shutdown);
    public Task<HiperwallSlot> SaveHiperwallSlotAsync(string token, SaveHiperwallSlotRequest request, CancellationToken ct) => _wall.SaveHiperwallSlotAsync(token, request, ct);
    public bool DeleteHiperwallSlot(string token, DeleteHiperwallSlotRequest request) => _wall.DeleteHiperwallSlot(token, request);
    public Task<MediaPayload> ReadHiperwallPreviewAsync(string token, HiperwallPreviewRequest request, CancellationToken ct) => _wall.ReadHiperwallPreviewAsync(token, request, ct);
}
