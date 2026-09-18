using System.Text.Json.Serialization;

namespace IntegratedContro.Core;

// A saved observation, never an instruction until explicitly restored.
public sealed record LightSlot(int Number, int Version, string Name, LightLayout Layout,
    StepSnapshot[] PowerStates, DateTimeOffset? SavedAt)
{
    public const int Count = 6;
    [JsonIgnore] public bool IsEmpty => SavedAt is null;
}
public sealed record SaveLightSlotRequest(long Generation, int Number, int ExpectedVersion,
    string Name, int LayoutVersion, LightPowerTarget[] Targets);
public sealed record DeleteLightSlotRequest(long Generation, int Number, int ExpectedVersion);
public sealed record RestoreLightSlotRequest(Guid RequestId, long Generation, int Number,
    int ExpectedVersion, int LayoutVersion);
