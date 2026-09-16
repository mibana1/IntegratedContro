using System.Globalization;
using System.Text;
using System.Xml.Linq;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

public sealed partial class HiperwallHttpReader
{
    public async Task<HiperwallWriteResult> WriteAsync(HiperwallConfiguration config, string? secret,
        HiperwallWireCommand command, CancellationToken ct)
    {
        static HiperwallWriteResult Unknown() => new(HiperwallSendState.Unknown,
            "전송 결과를 확인할 수 없습니다. 목록에서 대조하세요. 자동 재전송하지 않습니다.");
        try
        {
            var operation = BuildEdit(command);
            var xml = new XElement("Commands");
            if (config.Authentication == HiperwallAuthentication.Token)
            {
                if (string.IsNullOrEmpty(secret)) return new(HiperwallSendState.Rejected, "저장된 인증 토큰이 없습니다.");
                xml.Add(new XElement("auth", new XAttribute("type", "token"), new XElement("user", config.User), new XElement("token", secret)));
            }
            xml.Add(operation);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(config.Endpoint + "/"), "xmlcommand"))
                { Content = new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return status is 400 or 401 or 403 or 405 or 422 ? new(HiperwallSendState.Rejected,
                    "Controller가 명령을 거부했습니다. 쓰기 권한·대상·지원 형식을 확인하세요.") : Unknown();
            }
            if (response.Content.Headers.ContentLength > MaxBytes) return Unknown();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, ct);
                if (count == 0) break;
                if (buffer.Length + count > MaxBytes) return Unknown();
                buffer.Write(chunk, 0, count);
            }
            var body = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
            var root = ParseXml(body);
            if (root.DescendantsAndSelf().Any(e => e.Name.LocalName.Equals("Error", StringComparison.OrdinalIgnoreCase)))
                return new(HiperwallSendState.Rejected, "Controller가 명령 오류를 반환했습니다. 쓰기 권한·대상·지원 형식을 확인하세요.");
            if (root.Name != "Response") return Unknown();
            return new(HiperwallSendState.Acknowledged, "Controller 응답 확인 · 실제 표시·소리는 재조회와 현장에서 확인하세요.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ReadFailure or DecoderFallbackException)
        { return Unknown(); }
    }
    private static XElement BuildEdit(HiperwallWireCommand c)
    {
        static bool Identity(string? value, int max = 4096) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
        if (!Enum.IsDefined(c.Action) || c.Action is (HiperwallEditAction.CloseAll or HiperwallEditAction.RestoreSlot) || c.Layout is { IsValid: false } || c.Volume is < 0 or > 100)
            throw new ArgumentException("유효하지 않은 Hiperwall 명령입니다.");
        if (c.Action == HiperwallEditAction.MuteAll)
        {
            if (c.Muted is null) throw new ArgumentException("전체 음소거 값을 지정하세요.");
            return new XElement("action", new XAttribute("type", c.Muted.Value ? "mute-all" : "unmute-all"));
        }
        if (!Identity(c.InstanceId, 256)) throw new ArgumentException("인스턴스 ID가 필요합니다.");
        var xml = new XElement("command", new XAttribute("type", c.Action.ToString().ToLowerInvariant()));
        if (c.Action == HiperwallEditAction.Open)
        {
            if (c.Selector is not ("uuid" or "name") || !Identity(c.ContentValue) || c.Layout is null)
                throw new ArgumentException("콘텐츠 식별자와 위치·크기가 필요합니다.");
            xml.Add(new XElement(c.Selector, c.ContentValue));
        }
        xml.Add(new XElement("id", c.InstanceId));
        if (c.Layout is { } l)
        {
            if (!Identity(c.ZoneId)) throw new ArgumentException("Zone ID가 필요합니다.");
            static string N(double n) => (n == 0 ? 0 : n).ToString("R", CultureInfo.InvariantCulture);
            xml.Add(new XElement("zone", c.ZoneId), new XElement("x", N(l.X)), new XElement("y", N(-l.Y)),
                new XElement("boundsw", N(l.Width)), new XElement("boundsh", N(l.Height)), new XElement("boundsfill", "1"));
        }
        if (c.Volume is { } volume) xml.Add(new XElement("volume", volume));
        if (c.Muted is { } muted) xml.Add(new XElement("mute", muted ? "true" : "false"));
        if (c.Action == HiperwallEditAction.Change)
        {
            if (c.Layout is null && c.Volume is null && c.Muted is null) throw new ArgumentException("변경할 값을 지정하세요.");
            xml.Add(new XElement("queue", "0"), new XElement("time", "0"));
        }
        return xml;
    }
}
