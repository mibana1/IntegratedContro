using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record WallPreview(BitmapSource? Image, bool Failed, string Message, DateTimeOffset NextAt);

/// <summary>Visible-content cache: at most 64 decoded images / 24 MiB, two requests, memory only.</summary>
public sealed class HiperwallPreviewCoordinator
{
    private readonly HiperwallCanvas _canvas;
    private readonly Func<HiperwallViewModel?> _model;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, WallPreview> _cache = [];
    private CancellationTokenSource _lifetime = new();
    private bool _visible, _running;
    private long _epoch, _modelEpoch = -1;
    private HiperwallViewModel? _lastModel;
    public int CacheCount => _cache.Count;
    public long CacheBytes => _cache.Values.Sum(v => v.Image is { } image ? (long)image.PixelWidth * image.PixelHeight * 4 : 0);
    public int ActiveRequests { get; private set; }
    public HiperwallPreviewCoordinator(HiperwallCanvas canvas, Func<HiperwallViewModel?> model)
    {
        _canvas = canvas; _model = model; canvas.Preview = Lookup;
        _timer.Tick += async (_, _) => await Tick();
    }
    public static (string Selector, string Value)? Identity(HiperwallItemRow item)
    {
        if (item.Kind != HiperwallRowKind.Instance) return null;
        var uuid = item.Item.Fields.GetValueOrDefault("content.uuid");
        var name = item.Item.Fields.GetValueOrDefault("content.name");
        return !string.IsNullOrWhiteSpace(uuid) ? ("uuid", uuid) : !string.IsNullOrWhiteSpace(name) ? ("name", name) : null;
    }
    private static string Key((string Selector, string Value) identity) => identity.Selector + ":" + identity.Value;
    private WallPreview? Lookup(HiperwallItemRow item) => Identity(item) is { } id ? _cache.GetValueOrDefault(Key(id)) : null;
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) _timer.Start();
        else { _timer.Stop(); Reset(); }
    }
    public void RefreshContext()
    {
        var vm = _model();
        if (!ReferenceEquals(vm, _lastModel) || vm?.PreviewEpoch != _modelEpoch || vm?.CanPreview != true)
        { Reset(); _lastModel = vm; _modelEpoch = vm?.PreviewEpoch ?? -1; }
    }
    private void Reset()
    {
        _epoch++; _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _cache.Clear(); _canvas.InvalidateVisual();
    }
    public async Task Tick()
    {
        RefreshContext();
        var vm = _model();
        if (!_visible || _running || vm?.CanPreview != true) return;
        var visible = _canvas.VisibleInstances().Select(item => (Item: item, Identity: Identity(item)))
            .Where(x => x.Identity is not null).DistinctBy(x => Key(x.Identity!.Value)).Take(64).ToArray();
        var keys = visible.Select(x => Key(x.Identity!.Value)).ToHashSet();
        foreach (var key in _cache.Keys.Where(k => !keys.Contains(k)).ToArray()) _cache.Remove(key);
        var due = visible.Where(x => !_cache.TryGetValue(Key(x.Identity!.Value), out var cached) || cached.NextAt <= DateTimeOffset.UtcNow)
            .OrderBy(x => _cache.GetValueOrDefault(Key(x.Identity!.Value))?.NextAt ?? DateTimeOffset.MinValue).Take(2).ToArray();
        if (due.Length == 0) return;
        var epoch = _epoch; var ct = _lifetime.Token; _running = true;
        try { await Task.WhenAll(due.Select(async x =>
        {
            var id = x.Identity!.Value; var key = Key(id); var prior = _cache.GetValueOrDefault(key);
            var seconds = x.Item.Type.Contains("image", StringComparison.OrdinalIgnoreCase) ? 60 : 3;
            ActiveRequests++;
            try
            {
                var result = await vm.ReadPreview(id.Selector, id.Value, ct);
                if (ct.IsCancellationRequested || epoch != _epoch || !_visible ||
                    !_canvas.VisibleInstances().Any(item => Identity(item) is { } current && Key(current) == key)) return;
                var dimensions = PreviewImageLimits.Dimensions(result.Bytes);
                using var stream = new MemoryStream(result.Bytes, false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Min(1024, dimensions.Width); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                var size = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
                var previousSize = prior?.Image is { } image ? (long)image.PixelWidth * image.PixelHeight * 4 : 0;
                if (CacheBytes - previousSize + size > 24 * 1024 * 1024) throw MediaLimits.Invalid();
                _cache[key] = new(bitmap, false, "Controller 이미지 프리뷰", DateTimeOffset.UtcNow.AddSeconds(seconds));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (epoch == _epoch && !ct.IsCancellationRequested)
                    _cache[key] = new(prior?.Image, true, "프리뷰 갱신 실패", DateTimeOffset.UtcNow.AddSeconds(3));
            }
            finally { ActiveRequests--; }
        })); }
        finally { _running = false; _canvas.InvalidateVisual(); }
    }
}
