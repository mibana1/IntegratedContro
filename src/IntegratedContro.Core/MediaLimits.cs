using System.Text;
using System.Text.RegularExpressions;

namespace IntegratedContro.Core;

public static partial class MediaLimits
{
    public const int ImageBytes = 4 * 1024 * 1024;
    public const int SegmentBytes = 32 * 1024 * 1024;
    public const int PlaylistBytes = 128 * 1024;
    public static bool ValidAsset(string asset) => asset.Length <= 240 && AssetPattern().IsMatch(asset);
    [GeneratedRegex(@"^[a-zA-Z0-9_-][a-zA-Z0-9_.-]*\.(m3u8|ts|mp4|m4s)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPattern();
    [GeneratedRegex("URI=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex UriPattern();
    public static void ValidatePlaylist(byte[] bytes)
    {
        if (bytes.Length > PlaylistBytes) throw Invalid();
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) throw Invalid();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('#')) { if (!ValidAsset(line)) throw Invalid(); }
            else
            {
                foreach (Match uri in UriPattern().Matches(line))
                    if (!ValidAsset(uri.Groups[1].Value)) throw Invalid();
                // External keys, redirects and session metadata are outside the camera HLS profile.
                if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-SESSION", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-CONTENT-STEERING", StringComparison.Ordinal)) throw Invalid();
            }
        }
    }
    public static DomainException Invalid() => new("media_invalid", "지원하지 않거나 한도를 초과한 영상 응답입니다.", 502);
}
