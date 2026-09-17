using System.Collections.ObjectModel;
using System.Collections.Specialized;
using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private sealed class FakeContentLookup : IHiperwallContentLookup
    {
        public ObservableCollection<HiperwallItemRow> Items { get; } = [];
        public ReadOnlyObservableCollection<HiperwallItemRow> Contents { get; }
        public int ConfigurationVersion { get; set; }
        public FakeContentLookup() => Contents = new(Items);
    }

    private static async Task RunCameraContentLookup()
    {
        await using var host = new HostProcess(); await host.Initialize(60);
        await using var controller = new FakeHiperwallServer();
        controller.Contents = "<Objects><Object><name>같은 이름</name><uuid>first</uuid></Object><Object><name>같은 이름</name><uuid>second</uuid></Object><Object><name>폴더/UUID 없는 이미지.png</name></Object></Objects>";
        await using var media = new FakeHiperwallServer();
        media.Handler = request => Task.FromResult(new FakeHiperwallServer.Response("{}",
            request.Method == "GET" ? 404 : 200) { ContentType = "application/json" });
        var (_, login) = await host.Login();
        using var client = new HostClient(host.Endpoint, host.Fingerprint); client.SetToken(login.Token);
        var lease = await client.Post<Lease>("/api/lease/acquire");
        await client.Post<MediaSettingsView>("/api/media/settings",
            new SaveMediaSettingsRequest(lease.Generation, 0, media.Endpoint, media.Endpoint, "api", "reader", "fixture-api", "fixture-hls"));
        async Task<int> Configure(int version)
        {
            var settings = await client.Post<IntegratedContro.Core.HiperwallSettingsView>("/api/hiperwall/settings",
                new SaveHiperwallRequest(lease.Generation, version, "조회 계약 검증", controller.Endpoint,
                    HiperwallAuthentication.Token, "3", 3000, FakeHiperwallServer.FixtureSecret));
            await client.Post<IntegratedContro.Core.HiperwallView>("/api/hiperwall/refresh");
            return settings.Version;
        }
        var lookup = new FakeContentLookup();
        // This camera has no wall ViewModel or native video engine, including during mapping saves.
        var camera = new CameraViewModel(lookup, _ => throw new InvalidOperationException("Playback is outside this test"))
        { Generation = lease.Generation };
        try
        {
            var choices = camera.MappingContents;
            var changes = 0;
            ((INotifyCollectionChanged)choices).CollectionChanged += (_, _) => changes++;
            Require(((ICollection<HiperwallItemRow>)choices).IsReadOnly, "Camera can mutate the shared content catalog");
            lookup.ConfigurationVersion = await Configure(0);
            var inventory = await client.Get<IntegratedContro.Core.HiperwallView>("/api/hiperwall/status");
            foreach (var item in inventory.Contents.Items) lookup.Items.Add(new(item));
            Require(ReferenceEquals(choices, camera.MappingContents) && choices.Count == 3 && changes == 3,
                "Content updates did not reach the camera's stable observable lookup");
            camera.UpdateContext(client, Guid.NewGuid(), true, false, 1);
            camera.CameraName = "조회 인터페이스 카메라"; camera.Enabled = false;
            camera.ReadRtsp = () => "rtsp://127.0.0.1/fixture";
            camera.SelectedMapping = choices[1];
            await CameraExecute(camera, camera.SaveCommand);
            Require(camera.Cameras.SingleOrDefault() is { ContentSelector: "uuid", ContentValue: "second", Version: 1 },
                "Duplicate names lost UUID identity: " + camera.Message);

            camera.Selected = camera.Cameras.Single(); await CameraExecute(camera, camera.LoadCommand);
            await CameraExecute(camera, camera.SaveCommand);
            Require(camera.Cameras.Single() is { ContentSelector: "uuid", ContentValue: "second", Version: 2 },
                "Editing without a new selection lost the existing mapping: " + camera.Message);

            camera.SelectedMapping = choices[2];
            var latestVersion = await Configure(lookup.ConfigurationVersion);
            await CameraExecute(camera, camera.SaveCommand);
            Require(camera.Message.StartsWith("카메라 저장 오류:") && camera.Cameras.Single().Version == 2,
                "Stale lookup version was accepted or changed the stored camera");
            lookup.ConfigurationVersion = latestVersion;
            await CameraExecute(camera, camera.SaveCommand);
            Require(camera.Cameras.Single() is { ContentSelector: "name", ContentValue: "폴더/UUID 없는 이미지.png", Version: 3 },
                "Save did not read the latest lookup version or preserve the full name: " + camera.Message);

            camera.ClearMapping = true;
            await CameraExecute(camera, camera.SaveCommand);
            Require(camera.Cameras.Single() is { ContentSelector: null, ContentValue: null, Version: 4 },
                "Explicit clear did not override the selected mapping: " + camera.Message);
            lookup.Items.Clear();
            Require(ReferenceEquals(choices, camera.MappingContents) && choices.Count == 0 && changes == 4,
                "Lookup invalidation did not reach the existing camera collection");
            Console.WriteLine("PASS: camera mapping with an independent read-only observable lookup; UUID identity, retained mapping, full name, stale/current configuration version and explicit clear.");
        }
        finally { await camera.CloseAsync(); }
    }
}
