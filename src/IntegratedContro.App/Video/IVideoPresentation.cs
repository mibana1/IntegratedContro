using IntegratedContro.Core;

namespace IntegratedContro.App;

// App-only display contract. Core/Application never receive WPF surfaces or native engine objects.
public interface IVideoPresentation : IAsyncDisposable
{
    IVideoPlayer Player { get; }
    object Surface { get; }
    long DecodedFrames { get; }
    VideoPlaybackStatus LastStatus { get; }
}
