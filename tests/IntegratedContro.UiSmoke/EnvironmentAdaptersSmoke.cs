using System.Windows.Controls;
using IntegratedContro.App;
using IntegratedContro.Core;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunEnvironmentAdapters()
    {
        // No host, MediaMTX or native engine: a registered snapshot and an injected player are sufficient.
        using var client = new HostClient("https://127.0.0.1:1", new string('A', 64));
        var first = new FakeVideoPresentation();
        var camera = new CameraViewModel(new FakeContentLookup(), _ => Task.FromResult<IVideoPresentation>(first));
        try
        {
            camera.UpdateContext(client, Guid.NewGuid(), false, false, 0);
            camera.SetVisible(true);
            camera.Selected = new IntegratedContro.Core.CameraView(Guid.NewGuid(), 1, "fake engine camera", "", "test",
                true, CameraProvisioning.Ready, "fixture", DateTimeOffset.UtcNow, null, null);
            await CameraExecute(camera, camera.PlayCommand);
            await Wait(() => camera.PlaybackMessage == "fake playing", 2000);
            Require(ReferenceEquals(first.Surface, camera.VideoSurface) && first.Started && first.Muted, "Injected engine was not selected");
            camera.Muted = false; Require(!first.Muted, "Mute did not reach the replacement engine");
            Require(first.Source is { Uri.IsLoopback: true } && first.Source.Uri.UserInfo == "", "Engine bypassed the local relay");
            await camera.StopPlaybackAsync();
            Require(first.Disposed && first.Subscribers == 0 && camera.VideoSurface is null && !camera.IsPlaying, "Stopped player retained resources or callbacks");

            camera.PlayerFactory = _ => Task.FromException<IVideoPresentation>(new InvalidOperationException("injected engine init failure"));
            await CameraExecute(camera, camera.PlayCommand);
            Require(camera.PlaybackMessage.Contains("초기화하지 못했습니다") && camera.VideoSurface is null && !camera.IsPlaying,
                "Initialization failure was not shown or left a player");
            var next = new FakeVideoPresentation();
            camera.PlayerFactory = _ => Task.FromResult<IVideoPresentation>(next);
            await CameraExecute(camera, camera.PlayCommand);
            Require(next.Started, "Could not retry with another engine after initialization failure");
            next.Fail();
            await Wait(() => next.Disposed && !camera.IsPlaying, 2000);
            Require(camera.PlaybackMessage.Contains("실패"), "Asynchronous player failure was lost");

            var synchronous = new FakeVideoPresentation { ThrowOnPlay = true };
            camera.PlayerFactory = _ => Task.FromResult<IVideoPresentation>(synchronous);
            await CameraExecute(camera, camera.PlayCommand);
            Require(synchronous.Disposed && synchronous.Subscribers == 0 && !camera.IsPlaying, "Synchronous playback failure leaked resources");

            var creating = new TaskCompletionSource<IVideoPresentation>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            camera.PlayerFactory = _ => { entered.TrySetResult(); return creating.Task; };
            camera.PlayCommand.Execute(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            camera.SetVisible(false);
            var late = new FakeVideoPresentation();
            creating.SetResult(late);
            await Wait(() => late.Disposed && !camera.IsBusy, 2000);
            Require(!late.Started && camera.VideoSurface is null, "Late initialization started playback after the screen was hidden");
            Console.WriteLine("PASS: injected video engine, mute, stop, init failure/retry, async/sync failure, callback release, hide during initialization.");
        }
        finally { await camera.CloseAsync(); }
    }

    private sealed class FakeVideoPresentation : IVideoPresentation, IVideoPlayer
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<VideoPlaybackStatus>? _changed;
        public IVideoPlayer Player => this;
        public object Surface { get; } = new TextBlock { Text = "Fake video surface" };
        public long DecodedFrames => Started ? 1 : 0;
        public VideoPlaybackStatus LastStatus { get; private set; } = new(VideoPlaybackState.Stopped, "fake stopped");
        public bool Muted { get; set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnPlay { get; init; }
        public VideoSource? Source { get; private set; }
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public event Action<VideoPlaybackStatus>? StatusChanged { add => _changed += value; remove => _changed -= value; }
        public Task PlayAsync(VideoSource source, CancellationToken ct)
        {
            if (ThrowOnPlay) throw new InvalidOperationException("injected playback failure");
            Source = source; Started = true;
            LastStatus = new(VideoPlaybackState.Playing, "fake playing");
            _changed?.Invoke(LastStatus);
            return _completion.Task.WaitAsync(ct);
        }
        public void Fail() => _completion.TrySetException(new InvalidOperationException("injected async failure"));
        public ValueTask DisposeAsync() { _completion.TrySetResult(); Disposed = true; return ValueTask.CompletedTask; }
    }
}
