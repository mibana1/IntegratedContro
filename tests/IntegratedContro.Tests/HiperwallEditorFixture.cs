using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;

namespace IntegratedContro.Testing;

// Stateful loopback fixture for editor verification. All content and IDs are test data.
public sealed class HiperwallEditorFixture : IAsyncDisposable
{
    public FakeHiperwallServer Server { get; } = new();
    public ConcurrentQueue<XElement> Commands { get; } = new();
    public Func<XElement, FakeHiperwallServer.Response?>? CommandResponse { get; set; }
    private readonly object _gate = new();
    public HiperwallEditorFixture()
    {
        Server.Contents = "<Objects><Object type='stream'><name>폴더/영상 &amp; 소리</name><uuid>source-1</uuid><width>2560</width><height>1440</height></Object><Object type='image'><name>폴더/이미지 &amp; 지도</name><width>640</width><height>480</height></Object></Objects>";
        Server.Instances = "<Objects><Object type='stream'><name>폴더/영상 &amp; 소리</name><uuid>source-1</uuid><zone>zone-1</zone><Instance><id>external-1</id><position>-960,0</position><size>640,360</size><rotation>0</rotation><audio>75,unmuted</audio></Instance></Object></Objects>";
        Server.Handler = r => Task.FromResult(Handle(r));
    }
    private FakeHiperwallServer.Response Handle(FakeHiperwallServer.Request request)
    {
        lock (_gate)
        {
            if (request.Method == "GET") return Server.Default(request);
            var xml = XElement.Parse(request.Body);
            var operation = xml.Element("command") ?? xml.Element("action")!;
            var type = operation.Attribute("type")!.Value;
            if (type is "list" or "walls") return Server.Default(request);
            if (xml.Element("auth")?.Element("token")?.Value != FakeHiperwallServer.FixtureSecret)
                return new("<Error><code>403</code></Error>");
            if (type == "preview") return new("", 404);
            Commands.Enqueue(new XElement(operation));
            if (CommandResponse?.Invoke(operation) is { } custom) return custom;
            var instances = XElement.Parse(Server.Instances);
            var id = operation.Element("id")?.Value;
            var instance = instances.Descendants("Instance").SingleOrDefault(i => i.Element("id")?.Value == id);
            if (type == "open")
            {
                var source = XElement.Parse(Server.Contents).Elements("Object").Single(o => operation.Element("uuid") is { } uuid ? o.Element("uuid")?.Value == uuid.Value : o.Element("name")?.Value == operation.Element("name")?.Value);
                var obj = new XElement("Object", source.Attributes(), source.Elements().Where(e => e.Name == "name" || e.Name == "uuid"));
                obj.Add(new XElement("zone", operation.Element("zone")!.Value));
                instance = new XElement("Instance", new XElement("id", id), new XElement("rotation", "0"), new XElement("audio", "100,unmuted"));
                obj.Add(instance); instances.Add(obj);
            }
            if (type is "open" or "change")
            {
                if (instance is null) return new("<Error><code>404</code></Error>");
                if (operation.Element("x") is { } x)
                {
                    instance.SetElementValue("position", x.Value + "," + operation.Element("y")!.Value);
                    instance.SetElementValue("size", operation.Element("boundsw")!.Value + "," + operation.Element("boundsh")!.Value);
                    instance.Parent!.SetElementValue("zone", operation.Element("zone")!.Value);
                }
                var audio = (instance.Element("audio")?.Value ?? "100,unmuted").Split(',');
                if (operation.Element("volume") is { } volume) audio[0] = volume.Value;
                if (operation.Element("mute") is { } mute) audio[1] = mute.Value == "true" ? "muted" : "unmuted";
                instance.SetElementValue("audio", string.Join(',', audio));
            }
            if (type == "close")
            {
                if (instance is null) return new("<Error><code>404</code></Error>");
                var obj = instance.Parent!; instance.Remove(); if (!obj.Elements("Instance").Any()) obj.Remove();
            }
            if (type is "mute-all" or "unmute-all")
                foreach (var audio in instances.Descendants("audio")) audio.Value = audio.Value.Split(',')[0] + (type == "mute-all" ? ",muted" : ",unmuted");
            Server.Instances = instances.ToString(SaveOptions.DisableFormatting);
            return new("<Response />");
        }
    }
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
