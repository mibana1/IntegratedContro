using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    private readonly CancellationTokenSource _previewStopping = new();
    private readonly SemaphoreSlim _previewReads = new(2, 2);
    private static bool PreviewMatches(HiperwallList list, string selector, string value) =>
        value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl) && selector is "name" or "uuid" &&
        list.Items.Count(i => selector == "uuid" ? i.Id == value : i.Name == value) == 1;
    public async Task<MediaPayload> ReadHiperwallPreviewAsync(string token, HiperwallPreviewRequest request, CancellationToken ct)
    {
        HiperwallConfiguration config;
        lock (_gate)
        {
            Healthy(); HiperwallReaderSession(token);
            Require(_hiperwall is IHiperwallPreviewReader, "preview_unavailable", "호스트가 이미지 프리뷰를 지원하지 않습니다.", 503);
            Require(_state.Hiperwall is { } c && c.Version == request.ConfigurationVersion &&
                _hiperwallView?.ConfigurationVersion == c.Version && _hiperwallView.Contents.State == HiperwallListState.Available,
                "preview_inventory_required", "현재 Contents 목록을 새로 조회하세요.");
            Require(PreviewMatches(_hiperwallView!.Contents, request.Selector, request.Value),
                "preview_not_found", "현재 목록의 유일한 콘텐츠만 프리뷰를 조회할 수 있습니다.", 404);
            config = _state.Hiperwall!;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _previewStopping.Token);
        timeout.CancelAfter(Math.Min(config.TimeoutMs, 8000));
        Require(await _previewReads.WaitAsync(0, timeout.Token), "preview_busy", "프리뷰 조회 중입니다.", 429);
        try
        {
            MediaPayload result;
            try { result = await ((IHiperwallPreviewReader)_hiperwall!).ReadPreviewAsync(config,
                config.CredentialId is { } id ? _credentials!.Read(id) : null, request.Selector, request.Value, timeout.Token); }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            { throw new DomainException("preview_failed", "이미지 프리뷰 갱신에 실패했습니다.", 502); }
            lock (_gate)
            {
                HiperwallReaderSession(token);
                Require(_state.Hiperwall?.Version == config.Version && _hiperwallView?.Contents.State == HiperwallListState.Available &&
                    PreviewMatches(_hiperwallView.Contents, request.Selector, request.Value),
                    "preview_changed", "프리뷰 대상·설정이 변경되었습니다.");
                ct.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally { _previewReads.Release(); }
    }
}
