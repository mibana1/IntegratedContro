using IntegratedContro.App;
using IntegratedContro.Core;
using IntegratedContro.Testing;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    private static async Task RunHiperwallLayoutCapture(MainViewModel vm, HiperwallEditorFixture fixture,
        Func<AsyncCommand, Task> hiper)
    {
        var h = vm.Hiperwall;
        var original = fixture.Server.Instances;
        var walls = fixture.Server.Walls;
        var commands = fixture.Commands.Count;
        var missingZone = original.Replace("<zone>zone-1</zone>", "");
        async Task Reload(string instances, string? zones = null)
        {
            fixture.Server.Instances = instances; fixture.Server.Walls = zones ?? walls;
            await hiper(h.RefreshCommand);
        }

        // Reproduce capture directly after querying an existing source without Zone metadata.
        await Reload(missingZone);
        await hiper(h.CaptureLayoutCommand);
        Require(h.DraftPlacements.Count == 1 && h.DraftPlacements[0].ZoneId == "zone-1",
            "LIVE capture rejected a source with an unambiguous center: " + h.LayoutMessage);

        var mixed = missingZone.Replace("<position>-960,0</position>", "<position>-960.25,-100.5</position>")
            .Replace("<size>640,360</size>", "<size>4000.5,2200.25</size>")
            .Replace("</Objects>", """
                <Object type="image"><name>폴더/이미지 &amp; 지도</name><zone>   </zone>
                <Instance><id>image-1</id><position>500.25,-200.5</position><size>640.5,480.25</size><rotation>0</rotation><audio>0,muted</audio></Instance></Object>
                <Object type="stream"><name>폴더/영상 &amp; 소리</name><uuid>source-1</uuid><zone>zone-2</zone>
                <Instance><id>explicit-1</id><position>-900,-100</position><size>640,360</size><rotation>0</rotation></Instance></Object></Objects>
                """);
        await Reload(mixed);
        // Manual editing choices must not influence capture's per-instance Zone resolution.
        h.TargetZone = h.Zones[1]; h.DraftZone = h.Zones[1];
        await Execute(vm, vm.ReleaseCommand);
        await hiper(h.CaptureLayoutCommand);
        HiperwallPlacement[] expected =
        [
            new("uuid", "source-1", "zone-1", new(-960.25, 100.5, 4000.5, 2200.25), 75, false),
            new("name", "폴더/이미지 & 지도", "zone-2", new(500.25, 200.5, 640.5, 480.25), 0, true),
            new("uuid", "source-1", "zone-2", new(-900, 100, 640, 360))
        ];
        Require(h.DraftPlacements.SequenceEqual(expected),
            "Capture changed sources, geometry/audio or explicit Zone priority, or required a lease: " + h.LayoutMessage);
        await Execute(vm, vm.AcquireCommand);
        await hiper(h.LayoutSlots[0].SelectCommand); await hiper(h.SaveSlotCommand);
        Require(h.LayoutSlots[0].Snapshot?.Placements.SequenceEqual(expected) == true,
            "LIVE draft capture and slot save resolved the same inventory differently");
        h.LayoutName = "Zone 누락 LIVE 복사"; h.DurationMode = DisplayDurationMode.Continuous;
        await hiper(h.SaveLayoutCommand);
        Require(h.SavedLayouts.Count == 1 && h.SavedLayouts[0].Placements.SequenceEqual(expected),
            "Captured draft did not persist with the resolved Zones");
        await hiper(h.NewLayoutCommand); h.SelectedLayout = h.SavedLayouts[0]; await hiper(h.LoadLayoutCommand);
        Require(h.DraftPlacements.SequenceEqual(expected), "Captured draft did not round-trip");
        var version = h.DraftVersionLabel;
        h.SelectedPlacement = h.DraftPlacements[1]; var selection = h.SelectedPlacement;

        foreach (var kind in new[] { "overlap", "boundary", "outside", "unknown-zone", "missing-position", "rotation" })
        {
            var bad = missingZone.Replace("external-1", "rejected-1");
            var zones = walls;
            if (kind == "overlap") zones = walls.Replace("<left>0</left>", "<left>-1920.5</left>");
            if (kind == "boundary")
            {
                zones = walls.Replace("<left>-1920.5</left>", "<left>-1920</left>");
                bad = bad.Replace("<position>-960,0</position>", "<position>0,0</position>");
            }
            if (kind == "outside") bad = bad.Replace("<position>-960,0</position>", "<position>5000,-5000</position>");
            if (kind == "unknown-zone") bad = bad.Replace("<Instance>", "<zone>missing-zone</zone><Instance>");
            if (kind == "missing-position") bad = bad.Replace("<position>-960,0</position>", "");
            if (kind == "rotation") bad = bad.Replace("<rotation>0</rotation>", "<rotation>90</rotation>");
            // A valid item first detects accidental partial replacement of the existing draft.
            await Reload(original.Replace("</Objects>", bad.Replace("<Objects>", "")), zones);
            h.TargetZone = h.Zones[0]; h.DraftZone = h.Zones[0];
            await hiper(h.CaptureLayoutCommand);
            Require(h.DraftPlacements.SequenceEqual(expected) && ReferenceEquals(h.SelectedPlacement, selection) &&
                h.LayoutName == "Zone 누락 LIVE 복사" && h.DurationMode == DisplayDurationMode.Continuous && h.DraftVersionLabel == version,
                "Failed capture altered the existing draft: " + kind);
            Require(h.LayoutMessage.Contains("폴더/영상 & 소리") && h.LayoutMessage.Contains("rejected-1"),
                "Failed capture did not identify its content/instance: " + kind + " / " + h.LayoutMessage);
        }
        Require(fixture.Commands.Count == commands && h.SavedLayouts[0].Placements.SequenceEqual(expected),
            "Capture/save/rejection changed LIVE or the saved layout");
        await Reload(original); await hiper(h.DeleteLayoutCommand); await hiper(h.NewLayoutCommand);
        Console.WriteLine("LIVE layout capture PASS: omitted/blank/explicit Zones; slot parity; large/fractional geometry and audio; read-only capture; save/reload; ambiguous/outside/unknown/invalid rejection with complete draft preservation; no LIVE writes.");
    }
}