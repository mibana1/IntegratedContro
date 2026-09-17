namespace IntegratedContro.App;

// Client composition root. Engine selection does not belong to camera operations.
internal static class AppAdapters
{
    public static Task<IVideoPresentation> CreateVideoAsync(CancellationToken ct) => VlcVideoPresentation.CreateAsync(ct);
}
