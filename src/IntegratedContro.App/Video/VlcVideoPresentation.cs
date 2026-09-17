using IntegratedContro.Core;
using LibVLCSharp.WPF;

namespace IntegratedContro.App;

// The bridge between LibVLC's native player and its WPF display control.
public sealed class VlcVideoPresentation : IVideoPresentation
{
    private readonly VlcVideoPlayer _player;
    private readonly VideoView _view;
    private bool _disposed;
    private VlcVideoPresentation(VlcVideoPlayer player, VideoView view) { _player = player; _view = view; }
    public IVideoPlayer Player => _player;
    public object Surface => _view;
    public long DecodedFrames => _player.DecodedFrames;
    public VideoPlaybackStatus LastStatus => _player.LastStatus;

    // Called on the WPF dispatcher. Only engine creation runs on a worker thread.
    public static async Task<IVideoPresentation> CreateAsync(CancellationToken ct)
    {
        var player = await Task.Run(() => new VlcVideoPlayer(), ct);
        VideoView? view = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            view = new VideoView { MediaPlayer = player.NativePlayer };
            return new VlcVideoPresentation(player, view);
        }
        catch
        {
            view?.Dispose();
            await player.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _view.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _view.MediaPlayer = null;
        _view.Dispose();
        await _player.DisposeAsync();
    }
}
