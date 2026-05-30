namespace Veyra.Application.DTOs.Repository.Snapshots;

public sealed record RepositorySnapshotFileChangeDto(
    long SnapshotId,
    long FileIdentityId,
    long FileVersionId,
    string RelativePath,
    string Name,
    string ChangeKind,
    long CurrentSizeBytes,
    long PreviousSizeBytes,
    DateTime VersionCreatedAtUtc);
