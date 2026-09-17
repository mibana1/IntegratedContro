using System.Diagnostics;
using IntegratedContro.Core;
using LibVLCSharp.Shared;

namespace IntegratedContro.App;

/// <summary>Native engine boundary. Native callbacks only set signals; Stop/Dispose run off the UI/callback threads.</summary>
public sealed class VlcVideoPlayer : IVideoPlayer
{
    private readonly LibVLC _engine;
    public MediaPlayer NativePlayer { get; }
    private readonly CancellationTokenSource _stop = new();
    private Task? _play;
    private int _disposed;
    private bool _muted = true;
    public event Action<VideoPlaybackStatus>? StatusChanged;
    public long DecodedFrames { get; private set; }
    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (Volatile.Read(ref _disposed) == 0) NativePlayer.Mute = value; }
    }
    public VlcVideoPlayer()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _engine = new LibVLC("--no-video-title-show", "--quiet", "--no-snapshot-preview", "--network-caching=500");

        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer(_engine);
            player.Mute = true; player.EnableHardwareDecoding = true;
            NativePlayer = player;
        }
        catch
        {
            player?.Dispose(); _engine.Dispose(); _stop.Dispose();
            throw;
        }
    }
    public Task PlayAsync(VideoSource source, CancellationToken cancellationToken)
    {
        if (_play is not null || _disposed != 0) throw new InvalidOperationException("Create one player per playback.");
        if (source.Uri.Scheme != "http" || !source.Uri.IsLoopback || source.Uri.UserInfo != "")
            throw new ArgumentException("The native player accepts only the authenticated local relay.");
        _engine.SetDialogHandlers((title, text) => Task.CompletedTask,
            (dialog, title, text, defaultUser, askStore, token) =>
            {
                if (!_stop.IsCancellationRequested && text?.Contains("IntegratedControVideo", StringComparison.Ordinal) == true)
                    dialog.PostLogin(source.UserName, source.Password, false);
                else dialog.Dismiss();
                return Task.CompletedTask;
            },
            (dialog, title, text, type, cancel, first, second, token) => { dialog.Dismiss(); return Task.CompletedTask; },
            (dialog, title, text, cancelable, position, cancel, token) => { dialog.Dismiss(); return Task.CompletedTask; },
            (dialog, position, text) => Task.CompletedTask);
        _play = Task.Run(() => Run(source, cancellationToken));
        return _play;
    }
    public VideoPlaybackStatus LastStatus { get; private set; } = new(VideoPlaybackState.Stopped, "재생 전", 0);
    private void Status(VideoPlaybackState state, string message, int attempt = 0)
    {
        LastStatus = new(state, message, attempt); StatusChanged?.Invoke(LastStatus);
    }
    private async Task Run(VideoSource source, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        var ct = lifetime.Token;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 0)
                {
                    Status(VideoPlaybackState.Reconnecting, $"영상 연결 끊김 · 재연결 {attempt}/5", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(16, 1 << (attempt - 1))), ct);
                }
                else Status(VideoPlaybackState.Connecting, "HLS 영상 연결 중");
                using var media = new Media(_engine, source.Uri);

                media.AddOption(":network-caching=500");

                var ended = 0; var playing = false;
                void End(object? sender, EventArgs e) => Interlocked.Exchange(ref ended, 1);
                NativePlayer.EncounteredError += End; NativePlayer.EndReached += End;
                try
                {
                    if (!NativePlayer.Play(media)) continue;
                    NativePlayer.Mute = _muted;
                    var progress = Stopwatch.StartNew(); var lastFrames = 0;
                    while (!ct.IsCancellationRequested && Volatile.Read(ref ended) == 0)
                    {
                        await Task.Delay(250, ct).ConfigureAwait(false);
                        var stats = media.Statistics;
                        if (stats.DecodedVideo > lastFrames)
                        {
                            DecodedFrames += stats.DecodedVideo - lastFrames; lastFrames = stats.DecodedVideo; progress.Restart();
                            if (!playing) { playing = true; NativePlayer.Mute = _muted; Status(VideoPlaybackState.Playing, "실시간 영상 재생 중 · HLS 지연 포함"); }
                        }
                        if (progress.Elapsed > TimeSpan.FromSeconds(15)) break;
                    }
                }
                finally
                {
                    NativePlayer.EncounteredError -= End; NativePlayer.EndReached -= End;
                    NativePlayer.Stop(); // Stop joins libvlc's input thread. Never call it from an engine event.
                    NativePlayer.Media = null;
                }
            }
            Status(VideoPlaybackState.Failed, "자동 재연결 5회 종료 · 영상 상태 확인 후 재생을 다시 누르세요.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Status(VideoPlaybackState.Stopped, "재생 중지 · 영상 자원 정리"); }
        catch (Exception) { Status(VideoPlaybackState.Failed, "영상 엔진·코덱·HLS 구성을 확인하세요."); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_play is not null) await _play.ConfigureAwait(false);
        await Task.Run(() => { _engine.UnsetDialogHandlers(); NativePlayer.Dispose(); _engine.Dispose(); }).ConfigureAwait(false);
        _stop.Dispose();
    }
}
