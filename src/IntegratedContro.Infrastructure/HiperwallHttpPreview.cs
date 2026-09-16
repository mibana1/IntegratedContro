using System.Text;
using System.Xml.Linq;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

public sealed partial class HiperwallHttpReader
{
    public async Task<MediaPayload> ReadPreviewAsync(HiperwallConfiguration c, string? secret,
        string selector, string value, CancellationToken ct)
    {
        if (selector is not ("uuid" or "name") || string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            throw MediaLimits.Invalid();
        var xml = new XElement("Commands");
        if (c.Authentication == HiperwallAuthentication.Token)
        {
            if (string.IsNullOrEmpty(secret)) throw new DomainException("preview_auth", "Hiperwall 토큰을 확인하세요.", 503);
            xml.Add(new XElement("auth", new XAttribute("type", "token"), new XElement("user", c.User), new XElement("token", secret)));
        }
        xml.Add(new XElement("command", new XAttribute("type", "preview"), new XElement(selector, value)));
        using var request = new HttpRequestMessage(HttpMethod.Post, c.Endpoint + "/xmlcommand")
        { Content = new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new DomainException("preview_unavailable", "Controller 이미지 프리뷰를 사용할 수 없습니다.", 502);
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not ("image/jpeg" or "image/png")) throw MediaLimits.Invalid();
        var bytes = await MediaMtxHttpClient.ReadBounded(response, MediaLimits.ImageBytes, ct);
        PreviewImageLimits.Dimensions(bytes);
        return new(bytes, type);
    }
}
