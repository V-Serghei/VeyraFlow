namespace Veyra.Infrastructure.Data.Setup.Models.Recovery;

internal sealed record SnapshotEntryState(
    long SnapshotId,
    string RelativePath,
    string? ContentHashSha256,
    long SizeBytes);
