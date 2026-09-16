namespace IntegratedContro.Core;

public sealed record HiperwallSlot(int Number, int Version, int ConfigurationVersion,
    HiperwallPlacement[] Placements, DateTimeOffset? SavedAt)
{
    public const int Count = 6;
    public bool IsEmpty => SavedAt is null;
}
public sealed record SaveHiperwallSlotRequest(long Generation, int Number, int ExpectedVersion,
    int ConfigurationVersion, string ExpectedRevision);
public sealed record DeleteHiperwallSlotRequest(long Generation, int Number, int ExpectedVersion);
