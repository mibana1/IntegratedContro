using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Core;

namespace IntegratedContro.App;

/// <summary>One playback's loopback-only bridge to the pinned HTTPS host. No credentials in URLs or files.</summary>
public sealed class LoopbackVideoRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly Func<string, CancellationToken, Task<MediaPayload>> _fetch;
    private readonly string _authorization;
    private readonly Task _accept;
    private long _sequence;
    private int _disposed;
    public VideoSource Source { get; }
    public int ActiveRequests => _requests.Count;
    public LoopbackVideoRelay(Func<string, CancellationToken, Task<MediaPayload>> fetch)
    {
        _fetch = fetch; _listener.Start(4);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Source = new(new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/index.m3u8"), "play", password);
        _authorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("play:" + password));
        _accept = Accept();
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (_requests.Count >= 4) { socket.Dispose(); continue; }
                var id = Interlocked.Increment(ref _sequence);
                var task = Serve(socket);
                _requests[id] = task;
                _ = task.ContinueWith(completed => _requests.TryRemove(id, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Serve(TcpClient socket)
    {
        using (socket)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(18000);
            try
            {
                await using var stream = socket.GetStream();
                var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 8192)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0) return;
                    bytes.Add(one[0]);
                    if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var header = Encoding.ASCII.GetString(bytes.ToArray());
                var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (!header.EndsWith("\r\n\r\n", StringComparison.Ordinal) || lines.Length == 0) return;
                var first = lines[0].Split(' ');
                if (!lines.Skip(1).Any(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) &&
                    CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(l[(l.IndexOf(':') + 1)..].Trim()),
                        Encoding.ASCII.GetBytes(_authorization))))
                {
                    await Reply(stream, "401 Unauthorized", "text/plain", [], timeout.Token, "WWW-Authenticate: Basic realm=\"IntegratedControVideo\"\r\n");
                    return;
                }
                var asset = first.Length == 3 && first[0] == "GET" && first[1].StartsWith('/') ? first[1][1..] : "";
                if (!MediaLimits.ValidAsset(asset)) { await Reply(stream, "400 Bad Request", "text/plain", [], timeout.Token); return; }
                MediaPayload result;
                try { result = await _fetch(asset, timeout.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is not OutOfMemoryException)
                { await Reply(stream, "502 Bad Gateway", "text/plain", [], timeout.Token); return; }
                if (asset.EndsWith(".m3u8", StringComparison.Ordinal)) MediaLimits.ValidatePlaylist(result.Bytes);
                await Reply(stream, "200 OK", result.ContentType, result.Bytes, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException or DomainException) { }
        }
    }
    private static async Task Reply(Stream stream, string status, string type, byte[] bytes, CancellationToken ct, string extra = "")
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener.Stop();
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_requests.Values).ConfigureAwait(false);
        _stop.Dispose();
    }
}
