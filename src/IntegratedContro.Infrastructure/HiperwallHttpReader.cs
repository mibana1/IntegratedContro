using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Infrastructure;

/// <summary>Reference-code compatibility profile: GET /hello and POST /xmlcommand with typed inventory and editing operations.
/// Never accepts an arbitrary operation, URL path, or caller-provided XML. See docs/HIPERWALL_PROTOCOL.md.</summary>
public sealed partial class HiperwallHttpReader : IHiperwallReader, IHiperwallWriter, IHiperwallPreviewReader, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private const int MaxBytes = 4 * 1024 * 1024;
    private sealed class ReadFailure(HiperwallConnectionState state, string message) : Exception(message)
    { public HiperwallConnectionState State { get; } = state; }

    public async Task<HiperwallReading> ReadAsync(HiperwallConfiguration config, string? secret, CancellationToken ct)
    {
        HiperwallController? controller = null;
        try
        {
            var hello = await Send(config, null, secret, ct);
            controller = ParseHello(hello, config.Authentication);
            var contents = await ReadSection(config, "list", secret, ct);
            var wallResponse = await ReadSection(config, "walls", secret, ct);
            var opened = await ReadSection(config, "instances", secret, ct);
            var wallList = wallResponse.Walls ?? Unsupported("Wall 목록을 확인할 수 없습니다.");
            var zones = wallResponse.Zones ?? Unsupported("Zone 목록을 확인할 수 없습니다.");
            var list = contents.Contents ?? new(HiperwallListState.Failed, contents.Message, []);
            var errors = new[] { contents.Error, wallResponse.Error, opened.Error };
            var state = errors.Contains(HiperwallConnectionState.AuthenticationFailed) ? HiperwallConnectionState.AuthenticationFailed :
                errors.Contains(HiperwallConnectionState.TimedOut) ? HiperwallConnectionState.TimedOut :
                errors.Contains(HiperwallConnectionState.ConnectionFailed) ? HiperwallConnectionState.ConnectionFailed :
                contents.Error ?? wallResponse.Error ?? opened.Error ?? HiperwallConnectionState.Connected;
            var message = state == HiperwallConnectionState.Connected
                ? "목록을 조회했습니다. 콘텐츠와 열린 인스턴스를 선택하여 편집하세요."
                : contents.Error == state ? contents.Message : wallResponse.Error == state ? wallResponse.Message : opened.Message;
            return new(state, message, controller, wallList, zones, list) { Instances = opened.Instances! };
        }
        catch (ReadFailure e)
        {
            var list = new HiperwallList(e.State == HiperwallConnectionState.Unsupported ? HiperwallListState.Unsupported : HiperwallListState.Failed, e.Message, []);
            return new(e.State, e.Message, controller, list, list, list) { Instances = list };
        }
        catch (HttpRequestException)
        {
            var list = new HiperwallList(HiperwallListState.Failed, "Controller 연결이 끊겼거나 주소·포트·TLS 연결을 확인할 수 없습니다.", []);
            return new(HiperwallConnectionState.ConnectionFailed, list.Reason, controller, list, list, list) { Instances = list };
        }
        catch (IOException)
        {
            var list = new HiperwallList(HiperwallListState.Failed, "Controller 응답 수신 중 연결이 끊겼습니다.", []);
            return new(HiperwallConnectionState.ConnectionFailed, list.Reason, controller, list, list, list) { Instances = list };
        }
    }
    private sealed record Section(HiperwallList? Contents = null, HiperwallList? Walls = null, HiperwallList? Zones = null,
        HiperwallConnectionState? Error = null, string Message = "", HiperwallList? Instances = null);
    private async Task<Section> ReadSection(HiperwallConfiguration c, string action, string? secret, CancellationToken ct, bool detailed = true)
    {
        try
        {
            var xml = ParseXml(await Send(c, action, secret, ct, detailed));
            var error = xml.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == "Error");
            if (error is not null)
            {
                var code = error.Element("code")?.Value;
                throw StatusFailure(int.TryParse(code, out var value) ? value : 0);
            }
            var now = DateTimeOffset.UtcNow;
            if (action == "instances")
                return new(Instances: new(HiperwallListState.Available, "Controller list / filter=open 응답 · 인스턴스 ID 기준", ParseInstances(xml, secret), now));
            if (action == "list")
                return new(Contents: new(HiperwallListState.Available, "Controller list 응답 · 기본 필드", ParseItems(xml, "Objects", "Object", secret), now));
            if (xml.Name == "Zones")
                return new(Walls: Unsupported("Controller가 Zones 응답을 반환했습니다. 별도 Wall 목록은 제공되지 않았습니다."),
                    Zones: new(HiperwallListState.Available, "Controller walls 요청의 Zones 응답", ParseItems(xml, "Zones", "Zone", secret), now));
            if (xml.Name == "Walls" && !xml.Elements().Any() && !HasText(xml))
                return new(Walls: new(HiperwallListState.Available, "Controller가 빈 Walls 응답을 반환했습니다.", [], now),
                    Zones: Unsupported("Controller가 Walls 응답을 반환했습니다. Zone 목록이 제공되지 않았습니다."));
            if (xml.Name == "Walls" && xml.Elements().All(e => e.Name == "Wall") && !HasText(xml))
                return new(Walls: Unsupported("Walls 응답 수신. Wall 상세 필드·안정적인 식별자의 명세가 미확인되어 목록 해석을 지원하지 않습니다."),
                    Zones: Unsupported("Controller가 Walls 응답을 반환했습니다. Zone 목록이 제공되지 않았습니다."),
                    Error: HiperwallConnectionState.Unsupported, Message: "Wall 상세 응답 명세를 확인해야 합니다.");
            throw InvalidFormat();
        }
        catch (ReadFailure e)
        {
            // Read-only negotiation based on the response, never on a guessed release number.
            if (detailed && action is "list" or "instances" && e.State == HiperwallConnectionState.Unsupported)
                return await ReadSection(c, action, secret, ct, false);
            var list = new HiperwallList(e.State == HiperwallConnectionState.Unsupported ? HiperwallListState.Unsupported : HiperwallListState.Failed, e.Message, []);
            return action == "instances" ? new(Instances: list, Error: e.State, Message: e.Message)
                : action == "list" ? new(Contents: list, Error: e.State, Message: e.Message)
                : new(Walls: list, Zones: list, Error: e.State, Message: e.Message);
        }
    }
    private async Task<string> Send(HiperwallConfiguration c, string? action, string? secret, CancellationToken ct, bool detailed = true)
    {
        using var request = new HttpRequestMessage(action is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(new Uri(c.Endpoint + "/"), action is null ? "hello" : "xmlcommand"));
        if (action is not null)
        {
            if (action is not ("list" or "walls" or "instances")) throw new InvalidOperationException("Read operation required");
            var commands = new XElement("Commands");
            if (c.Authentication == HiperwallAuthentication.Token)
            {
                if (string.IsNullOrEmpty(secret)) throw new ReadFailure(HiperwallConnectionState.AuthenticationFailed, "저장된 토큰이 없습니다.");
                commands.Add(new XElement("auth", new XAttribute("type", "token"),
                    new XElement("user", c.User), new XElement("token", secret)));
            }
            commands.Add(new XElement("action", new XAttribute("type", action == "instances" ? "list" : action),
                action == "instances" ? new XElement("filter", "open") : null,
                detailed && action is "list" or "instances" ? new XElement("fields", action == "list"
                    ? "label,width,height,said,zone"
                    : "type,width,height,said,label,zone,instances(position,size,rotation,transparency,layer,audio,showlabel,borderRGB,bordervis)") : null));
            request.Content = new StringContent(commands.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml");
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw StatusFailure((int)response.StatusCode);
        var limit = action is null ? 4096 : MaxBytes;
        if (response.Content.Headers.ContentLength > limit) throw InvalidFormat();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, ct);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw InvalidFormat();
            buffer.Write(chunk, 0, count);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw InvalidFormat(); }
    }
    private static ReadFailure StatusFailure(int code) => code switch
    {
        401 or 403 or 440 => new(HiperwallConnectionState.AuthenticationFailed, "Controller 인증 또는 조회 권한이 거부되었습니다. 인증 방식·사용자·토큰·조회 권한을 확인하세요."),
        408 or 504 => new(HiperwallConnectionState.TimedOut, "Controller에서 시간 초과를 반환했습니다."),
        400 or 404 or 405 or 501 => new(HiperwallConnectionState.Unsupported, "읽기 API가 없거나 요청 형식이 지원되지 않습니다. 현장 버전의 명세를 확인하세요."),
        _ => new(HiperwallConnectionState.ConnectionFailed, "Controller가 읽기 요청 실패를 반환했습니다. 호스트와 Controller의 연결 상태를 확인하세요.")
    };
    private static ReadFailure InvalidFormat() => new(HiperwallConnectionState.Unsupported, "지원되지 않거나 잘못된 응답 형식입니다. 원문과 인증 응답은 표시하지 않습니다.");
    private static HiperwallList Unsupported(string reason) => new(HiperwallListState.Unsupported, reason, []);
    private static HiperwallController ParseHello(string body, HiperwallAuthentication configured)
    {
        var fields = body.Trim().Split(',').Select(x => x.Trim()).ToArray();
        if (fields.Length != 4 || fields[0] != "Hiperwall" ||
            !Regex.IsMatch(fields[1], @"^(?:[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(?:\.[0-9]{1,9})?|[0-9]{4} R[0-9]{1,2}(?:\.[0-9]{1,3})?)(?: \(build [0-9]{1,9}\))?$") ||
            fields[3] is not ("Default" or "Primary" or "Shadow")) throw InvalidFormat();
        if (fields[2].StartsWith("Crypto:", StringComparison.Ordinal))
            throw new ReadFailure(HiperwallConnectionState.Unsupported, "Controller는 Crypto 인증을 사용합니다. 이 단계는 None/Token만 지원합니다.");
        if (fields[2] is not ("None" or "Token")) throw InvalidFormat();
        if (fields[2] != configured.ToString())
            throw new ReadFailure(HiperwallConnectionState.AuthenticationFailed, "설정한 인증 방식과 Controller의 응답이 다릅니다.");
        // Never infer enhanced-list or other features from version numbers.
        return new(fields[1], fields[2], fields[3]);
    }
    private static XElement ParseXml(string body)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxBytes, IgnoreComments = true, IgnoreProcessingInstructions = true };
            using (var check = XmlReader.Create(new StringReader(body), settings))
                while (check.Read()) if (check.Depth > 16) throw InvalidFormat();
            using var reader = XmlReader.Create(new StringReader(body), settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is null || doc.Root.DescendantsAndSelf().Any(e => e.Ancestors().Take(17).Count() > 16)) throw InvalidFormat();
            return doc.Root;
        }
        catch (XmlException) { throw InvalidFormat(); }
    }
    private static bool HasText(XElement element) => element.Nodes().OfType<XText>().Any(x => !string.IsNullOrWhiteSpace(x.Value));
    private static HiperwallItem[] ParseItems(XElement root, string rootName, string itemName, string? secret)
    {
        if (root.Name != rootName || HasText(root) || root.Elements().Any(e => e.Name != itemName)) throw InvalidFormat();
        var result = new List<HiperwallItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string[] allowed = itemName == "Object" ? ["name", "uuid", "type", "label", "width", "height"] :
            ["id", "name", "color", "left", "top", "width", "height", "zonegridh", "zonegridv"];
        foreach (var element in root.Elements())
        {
            if (result.Count >= 10000 || HasText(element)) throw InvalidFormat();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in element.Elements().Where(e => allowed.Contains(e.Name.ToString())))
            {
                if (field.HasElements || field.Value.Length > 4096 || !values.TryAdd(field.Name.LocalName, field.Value)) throw InvalidFormat();
            }
            if (itemName == "Object" && element.Attribute("type") is { } type) values["type"] = type.Value;
            var id = values.GetValueOrDefault(itemName == "Object" ? "uuid" : "id");
            if (string.IsNullOrWhiteSpace(id)) id = null;
            if (id is not null && !ids.Add(id)) throw InvalidFormat();
            var name = values.GetValueOrDefault("name", "");
            if (itemName == "Object" && string.IsNullOrWhiteSpace(name) || itemName == "Zone" && id is null && string.IsNullOrWhiteSpace(name)) throw InvalidFormat();
            foreach (var key in new[] { "left", "top", "width", "height", "zonegridh", "zonegridv" })
                if (values.TryGetValue(key, out var v) && (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)))
                    throw InvalidFormat();
            string Clean(string v) => string.IsNullOrEmpty(secret) ? v : v.Replace(secret, "[인증정보 생략]", StringComparison.Ordinal);
            result.Add(new(id is null ? null : Clean(id), Clean(name), values.ToDictionary(p => p.Key, p => Clean(p.Value))));
        }
        return result.ToArray();
    }
    private static HiperwallItem[] ParseInstances(XElement root, string? secret)
    {
        if (root.Name != "Objects" || HasText(root) || root.Elements().Any(e => e.Name != "Object")) throw InvalidFormat();
        var result = new List<HiperwallItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string Clean(string value) => string.IsNullOrEmpty(secret) ? value : value.Replace(secret, "[인증정보 생략]", StringComparison.Ordinal);
        static Dictionary<string, string> Fields(XElement element, string[] allowed)
        {
            if (HasText(element)) throw InvalidFormat();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in element.Elements().Where(e => allowed.Contains(e.Name.ToString())))
                if (field.HasElements || field.Value.Length > 4096 || !values.TryAdd(field.Name.LocalName, field.Value)) throw InvalidFormat();
            return values;
        }
        foreach (var obj in root.Elements())
        {
            var content = Fields(obj, ["name", "uuid", "type", "label", "zone"]);
            if (obj.Attribute("type") is { } type)
            {
                if (type.Value.Length > 4096) throw InvalidFormat();
                content["type"] = type.Value;
            }
            // A filter-ignored basic inventory must not be mistaken for an empty instance inventory.
            if (!obj.Elements("Instance").Any()) throw new ReadFailure(HiperwallConnectionState.Unsupported,
                "열린 콘텐츠 응답에 Instance 항목이 없습니다. 해당 버전의 open 필터·인스턴스 조회 지원을 확인하세요.");
            foreach (var instance in obj.Elements("Instance"))
            {
                if (result.Count >= 10000) throw InvalidFormat();
                var fields = Fields(instance, ["id", "position", "size", "rotation", "transparency", "layer",
                    "showlabel", "borderRGB", "bordervis", "audio"]);
                var id = fields.GetValueOrDefault("id");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !ids.Add(id)) throw InvalidFormat();
                foreach (var pair in new[] { "position", "size" })
                    if (fields.TryGetValue(pair, out var value) &&
                        (!HiperwallGeometry.TryPair(value, out var a, out var b) || pair == "size" && (a <= 0 || b <= 0))) throw InvalidFormat();
                foreach (var key in new[] { "rotation", "transparency", "layer", "bordervis" })
                    if (fields.TryGetValue(key, out var value) && !HiperwallGeometry.TryNumber(value, out _)) throw InvalidFormat();
                foreach (var field in content) fields.Add("content." + field.Key, field.Value);
                result.Add(new(Clean(id), Clean(content.GetValueOrDefault("name", "")),
                    fields.ToDictionary(p => p.Key, p => Clean(p.Value))));
            }
        }
        return result.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
