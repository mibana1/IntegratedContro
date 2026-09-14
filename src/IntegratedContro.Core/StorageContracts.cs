namespace IntegratedContro.Core;

public sealed record HistoryItem<T>(long Sequence, T Value);
public sealed record HistoryPage<T>(HistoryItem<T>[] Items, long? NextBefore);
public sealed record HistoryRequest(long? Before = null, int Limit = 50);
public sealed record BackupFile(string Name, long Length, string Sha256);
public sealed record BackupManifest(int FormatVersion, int DatabaseVersion, Guid SiteId, long Revision,
    DateTimeOffset CreatedAt, string Purpose, BackupFile[] Files, bool IncludesHostConfiguration);
public sealed record BackupResult(string Directory, BackupManifest Manifest);
