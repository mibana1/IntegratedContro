using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace IntegratedContro.Testing;

// A loopback-only fixture. It is deliberately not a Hiperwall simulator or evidence of device compatibility.
public sealed class FakeHiperwallServer : IAsyncDisposable
{
    public sealed record Request(string Method, string Path, string Body);
    public sealed record Response(string Body, int Status = 200, int DelayMs = 0, bool Disconnect = false)
    { public byte[]? Bytes { get; init; } public string ContentType { get; init; } = "application/xml; charset=utf-8"; }
    public const string FixtureSecret = "fixture-only-토큰<&>";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ConcurrentBag<Task> _connections = [];
    public ConcurrentQueue<string> Operations { get; } = new();
    public string Endpoint { get; }
    public string Hello { get; set; } = "Hiperwall,2026 R2,Token,Primary";
    public string Contents { get; set; } = "<Objects><Object type=\"image\"><name>한글 중복 이름</name><uuid>uuid-1</uuid></Object><Object type=\"image\"><name>한글 중복 이름</name><uuid>uuid-2</uuid></Object><Object type=\"image\"><name>식별자 없는 이미지</name></Object></Objects>";
    public string Instances { get; set; } = "<Objects><Object type=\"image\"><name>한글 중복 이름</name><uuid>uuid-1</uuid><Instance><id>instance-1</id><position>-1440.5,-270</position><size>640,360</size><rotation>0</rotation><layer>1</layer><audio>50,unmuted</audio></Instance><Instance><id>instance-2</id><position>960,-540</position><size>1280,720</size><layer>2</layer></Instance></Object></Objects>";
    public string Walls { get; set; } = "<Zones><Zone><id>zone-1</id><name>관제 구역</name><left>-1920.5</left><top>0</top><width>1920</width><height>1080</height></Zone><Zone><id>zone-2</id><name>관제 구역</name><left>0</left><top>0</top><width>1920</width><height>1080</height></Zone></Zones>";
    public Func<Request, Task<Response>>? Handler { get; set; }
    public FakeHiperwallServer()
    {
        _listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Accept();
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(Respond(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Respond(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                while (header.Length < 16384)
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                    header.WriteByte(one[0]);
                    if (header.Length >= 4 && Encoding.ASCII.GetString(header.GetBuffer(), (int)header.Length - 4, 4) == "\r\n\r\n") break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                var length = lines.Where(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(x => int.Parse(x.Split(':')[1])).FirstOrDefault();
                if (length > 262144) return;
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, _stop.Token);
                var request = new Request(first[0], first[1], Encoding.UTF8.GetString(body));
                var response = Handler is null ? Default(request) : await Handler(request);
                if (response.DelayMs > 0) await Task.Delay(response.DelayMs, _stop.Token);
                if (response.Disconnect) return;
                var payload = response.Bytes ?? Encoding.UTF8.GetBytes(response.Body);
                var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Fixture\r\nContent-Type: {response.ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(prefix, _stop.Token); await stream.WriteAsync(payload, _stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    public Response Default(Request request)
    {
        if (request.Method == "GET" && request.Path == "/hello")
        { Operations.Enqueue("GET /hello"); return new(Hello); }
        if (request.Method != "POST" || request.Path != "/xmlcommand") throw new InvalidOperationException("Unexpected fixture path/method");
        var xml = XElement.Parse(request.Body);
        if (xml.Element("command")?.Attribute("type")?.Value == "preview")
            return new Response("", 404);
        var action = xml.Element("action")?.Attribute("type")?.Value;
        if (xml.Name != "Commands" || xml.Elements("command").Any() || action is not ("list" or "walls") ||
            xml.Elements("action").Count() != 1 || xml.Element("action")!.Elements().Any(e =>
                action != "list" || e.HasElements || !(e.Name == "filter" && e.Value == "open" || e.Name == "fields" && e.Value is
                    "label,width,height,said,zone" or "type,width,height,said,label,zone,instances(position,size,rotation,transparency,layer,audio,showlabel,borderRGB,bordervis)")) ||
            xml.Element("action")!.Elements("filter").Count() > 1 || xml.Element("action")!.Elements("fields").Count() > 1)
            throw new InvalidOperationException("A non-read action reached the fixture");
        var opened = xml.Element("action")!.Element("filter") is not null;
        Operations.Enqueue(opened ? "list/open" : action);
        if (Hello.Contains(",Token,") && (xml.Element("auth")?.Element("user")?.Value != "3" ||
            xml.Element("auth")?.Element("token")?.Value != FixtureSecret))
            return new("<Error><code>403</code><reason>fixture secret must never be surfaced</reason></Error>");
        return new(opened ? Instances : action == "list" ? Contents : Walls);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _loop;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
