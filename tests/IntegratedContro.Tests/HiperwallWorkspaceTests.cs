using System.Text.Json;
using IntegratedContro.Core;
using IntegratedContro.Infrastructure;
using IntegratedContro.Testing;

namespace IntegratedContro.Tests;

public sealed class HiperwallWorkspaceTests
{
    private static HiperwallConfiguration Config(string endpoint) => new(1, "fixture", endpoint, HiperwallAuthentication.Token, "3", 3000, null);
    [Fact]
    public async Task Open_filter_has_separate_instance_ids_and_real_geometry_fields()
    {
        await using var server = new FakeHiperwallServer();
        using var reader = new HiperwallHttpReader();
        var value = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(HiperwallListState.Available, value.Instances.State);
        Assert.NotNull(value.Instances.SucceededAt);
        Assert.Equal(new[] { "instance-1", "instance-2" }, value.Instances.Items.Select(x => x.Id));
        Assert.Equal(value.Instances.Items[0].Name, value.Instances.Items[1].Name);
        Assert.All(value.Instances.Items, item => Assert.Equal("uuid-1", item.Fields["content.uuid"]));
        Assert.True(HiperwallGeometry.TryInstance(value.Instances.Items[0], out var rect, out _));
        Assert.Equal(new HiperwallRectangle(-1760.5, 90, 640, 360), rect);
        Assert.True(HiperwallGeometry.TryZone(value.Zones.Items[0], out var zone, out _));
        Assert.Equal(new HiperwallRectangle(-1920.5, 0, 1920, 1080), zone);
        Assert.DoesNotContain(FakeHiperwallServer.FixtureSecret, JsonSerializer.Serialize(value));
        Assert.Equal(new[] { "GET /hello", "list", "walls", "list/open" }, server.Operations.ToArray());
    }
    [Theory]
    [InlineData("<Objects/>", HiperwallListState.Available)]
    [InlineData("<Objects><Object><name>filter ignored</name><uuid>a</uuid></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object><Instance><id>i</id><position>NaN,0</position></Instance></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object><Instance><id>i</id><size>0,1</size></Instance></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object><Instance><id>i</id></Instance><Instance><id>i</id></Instance></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Objects><Object><Instance><position>0,0</position><size>1,1</size></Instance></Object></Objects>", HiperwallListState.Unsupported)]
    [InlineData("<!DOCTYPE Objects [<!ENTITY outside SYSTEM 'http://127.0.0.1:1/secret'>]><Objects>&outside;</Objects>", HiperwallListState.Unsupported)]
    [InlineData("<Error><code>403</code><response>DO-NOT-SHOW</response></Error>", HiperwallListState.Failed)]
    public async Task Empty_unsupported_ambiguous_and_unsafe_instance_responses(string xml, HiperwallListState expected)
    {
        await using var server = new FakeHiperwallServer { Instances = xml };
        using var reader = new HiperwallHttpReader();
        var value = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(expected, value.Instances.State);
        Assert.Empty(value.Instances.Items);
        Assert.Equal(3, value.Contents.Items.Length);
        Assert.DoesNotContain("DO-NOT-SHOW", JsonSerializer.Serialize(value));
    }
    [Fact]
    public async Task Missing_coordinates_do_not_invent_a_position_or_zone_association()
    {
        await using var server = new FakeHiperwallServer
        {
            Instances = "<Objects><Object type='image'><name>좌표 없음</name><uuid>u</uuid><Instance><id>i</id></Instance></Object></Objects>",
            Walls = "<Zones><Zone><id>z</id><name>좌표 없음</name></Zone></Zones>"
        };
        using var reader = new HiperwallHttpReader();
        var value = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        var instance = Assert.Single(value.Instances.Items);
        Assert.False(HiperwallGeometry.TryInstance(instance, out _, out var why));
        Assert.Contains("position/size", why);
        Assert.False(instance.Fields.ContainsKey("zone"));
        Assert.False(instance.Fields.ContainsKey("content.zone"));
        Assert.False(HiperwallGeometry.TryZone(Assert.Single(value.Zones.Items), out _, out _));
    }
    [Theory]
    [InlineData("1e309,0", "10,10", null)]
    [InlineData("0,0", "-1,10", null)]
    [InlineData("0,0", "10,10", "45")]
    [InlineData("1e12,0", "10,10", null)]
    [InlineData("0,0,1", "10,10", null)]
    public void Invalid_or_unconfirmed_geometry_remains_outside_the_canvas(string position, string size, string? rotation)
    {
        var fields = new Dictionary<string, string> { ["position"] = position, ["size"] = size };
        if (rotation is not null) fields["rotation"] = rotation;
        Assert.False(HiperwallGeometry.TryInstance(new("i", "이름", fields), out _, out var reason));
        Assert.NotEmpty(reason);
    }
    [Fact]
    public async Task Unsupported_optional_instance_api_is_not_an_empty_success()
    {
        await using var server = new FakeHiperwallServer();
        server.Handler = request => Task.FromResult(request.Body.Contains("<filter>open</filter>", StringComparison.Ordinal)
            ? new FakeHiperwallServer.Response("PRIVATE-ERROR", 404) : server.Default(request));
        using var reader = new HiperwallHttpReader();
        var value = await reader.ReadAsync(Config(server.Endpoint), FakeHiperwallServer.FixtureSecret, default);
        Assert.Equal(HiperwallConnectionState.Unsupported, value.State);
        Assert.Equal(HiperwallListState.Unsupported, value.Instances.State);
        Assert.Equal(3, value.Contents.Items.Length);
        Assert.DoesNotContain("PRIVATE-ERROR", JsonSerializer.Serialize(value));
    }
}
