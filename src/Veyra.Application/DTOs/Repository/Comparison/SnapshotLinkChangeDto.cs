namespace Veyra.Application.DTOs;

public sealed record SnapshotLinkChangeDto(
    long FileIdentityId,
    long FileVersionId,
    string RelativePath,
    string Name,
    string ChangeKind,
    long CurrentSizeBytes,
    long PreviousSizeBytes,
    DateTime VersionCreatedAtUtc);
